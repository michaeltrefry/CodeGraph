import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeApiService } from './knowledge-api.service';
import { ReviewQueuePageComponent } from './review-queue-page.component';
import { HUMAN, challenge, entry, reviewItem } from './knowledge-fixtures';
import { ReviewItem } from './knowledge.models';

describe('ReviewQueuePageComponent', () => {
  const proposed = reviewItem();
  const challenged = reviewItem({
    kind: 'challenge', priority: 'highest', recurrence: 2,
    entry: { id: 'r-1', kind: 'lesson', summary: 'Never push to main', confidence: 'high', version: 5, rule: true },
    author: HUMAN, challenges: [challenge()]
  });
  const contradiction = reviewItem({
    kind: 'contradiction',
    entry: { id: 'r-1', kind: 'lesson', summary: 'Never push to main', confidence: 'high', version: 5 },
    other: { id: 'l-2', kind: 'lesson', summary: 'Push hotfixes to main', confidence: 'medium', version: 1 },
    author: HUMAN, other_author: HUMAN
  });
  const decision = reviewItem({ kind: 'autonomous_decision', entry: { id: 'd-1', kind: 'decision', summary: 'Use SSE', confidence: 'medium', version: 2, made_autonomously: true } });
  let api: { [K in 'listProjects' | 'reviewQueue' | 'record' | 'promote' | 'challenge']: ReturnType<typeof vi.fn> };

  async function create(items: ReviewItem[] = [challenged, proposed, contradiction, decision]) {
    api.reviewQueue.mockReturnValue(of({ items }));
    await TestBed.configureTestingModule({
      imports: [ReviewQueuePageComponent],
      providers: [{ provide: KnowledgeApiService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewQueuePageComponent);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) await new Promise(resolve => setTimeout(resolve));
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    api = {
      listProjects: vi.fn().mockReturnValue(of([{ id: 'p', key: 'CG', kind: 'project', title: 'CodeGraph' }])),
      reviewQueue: vi.fn(),
      record: vi.fn().mockReturnValue(of({ created: false, entry: entry({ id: 'd-1', version: 3 }) })),
      promote: vi.fn().mockReturnValue(of({ entry: entry(), changed: true })),
      challenge: vi.fn().mockReturnValue(of({ created: false, challenge: challenge({ status: 'upheld' }), rule: entry() }))
    };
  });

  it('lists autonomous decisions, open challenges, proposed rules and contradictions with their actors, and says nothing waits on it', async () => {
    const fixture = await create();
    expect(api.reviewQueue).toHaveBeenCalledWith('CG', 20, undefined);
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Nothing here blocks work');
    for (const label of ['Challenged rule', 'Proposed rule', 'Contradicting lessons', 'Autonomous decision']) expect(text).toContain(label);
    expect(text).toContain('Release branches need it');
    expect(text).toContain('Push hotfixes to main');
    expect(text).toContain('claude-code / opus @ dev-mac');
    expect(text).toContain('michael');
  });

  it('approves in one click: promote approve for a proposed rule, acknowledge for a decision', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;

    await page.approve(proposed);
    expect(api.promote).toHaveBeenCalledWith('l-1', { expected_version: 2, action: 'approve' });
    await page.approve(decision);
    expect(api.record).toHaveBeenCalledWith({ acknowledge: { id: 'd-1', expected_version: 2 } });
    expect(api.reviewQueue).toHaveBeenCalledTimes(3);
  });

  it('rejects with a reason: promote reject for a proposed rule, retract for a decision', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;

    page.open(proposed, 'reject');
    await page.submit(proposed);
    expect(page.error()).toBe('Give the reason it should not be a rule.');
    page.reason = 'Too noisy';
    await page.submit(proposed);
    expect(api.promote).toHaveBeenCalledWith('l-1', { expected_version: 2, action: 'reject', reason: 'Too noisy' });

    page.open(decision, 'reject');
    page.reason = 'Polling is simpler';
    await page.submit(decision);
    expect(api.record).toHaveBeenCalledWith({ retract: { id: 'd-1', expected_version: 2, reason: 'Polling is simpler' } });
  });

  it('amends: a revision, and for a decision an acknowledgement at the new version', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.open(decision, 'amend');
    page.amendSummary = 'Use SSE with a polling fallback';
    await page.submit(decision);

    expect(api.record.mock.calls[0][0]).toEqual({ revise: { id: 'd-1', expected_version: 2, summary: 'Use SSE with a polling fallback' } });
    expect(api.record.mock.calls[1][0]).toEqual({ acknowledge: { id: 'd-1', expected_version: 3 } });
    expect(page.notice()).toBe('Amended and acknowledged.');
  });

  it('resolves a challenge and retracts either side of a contradiction', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;

    page.open(challenged, 'uphold', { challengeId: 'c-1' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('form.kn-form')).not.toBeNull();
    page.reason = 'Still right';
    await page.submit(challenged);
    expect(api.challenge).toHaveBeenCalledWith({ resolve: { challenge_id: 'c-1', expected_version: 5, outcome: 'uphold', reason: 'Still right' } });

    page.open(contradiction, 'retract', { side: 'other' });
    page.reason = 'Hotfixes go through release branches';
    await page.submit(contradiction);
    expect(api.record).toHaveBeenCalledWith({ retract: { id: 'l-2', expected_version: 1, reason: 'Hotfixes go through release branches' } });
  });

  it('reloads the queue after a conflict', async () => {
    const fixture = await create();
    api.promote.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Version 3 is current.' } })));
    await fixture.componentInstance.approve(proposed);
    expect(fixture.componentInstance.error()).toContain('reloaded');
    expect(api.reviewQueue).toHaveBeenCalledTimes(2);
  });

  it('says revised, not acknowledged, when only the first write of an amend lands, and reloads the queue', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    api.record
      .mockReturnValueOnce(of({ created: false, entry: entry({ id: 'd-1', version: 3 }) }))
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503, error: { code: 'busy', message: 'The store is busy.' } })));
    page.open(decision, 'amend');
    page.amendSummary = 'Use SSE with a fallback';

    await page.submit(decision);

    expect(page.error()).toBe('Revised, not acknowledged: The store is busy. Approve it to acknowledge the revised decision.');
    expect(page.notice()).toBeNull();
    expect(page.form()).toBeNull();
    expect(api.reviewQueue).toHaveBeenCalledTimes(2);
  });

  it('counts an acknowledge refused as already acknowledged as done', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    api.record.mockReturnValueOnce(throwError(() => new HttpErrorResponse({
      status: 400, error: { code: 'invalid_argument', message: 'Decision d-1 was already acknowledged. Nothing was changed.' }
    })));

    await page.approve(decision);

    expect(page.error()).toBeNull();
    expect(page.notice()).toBe('Acknowledged: the decision stands.');
  });

  it('amends the second lesson of a contradiction and folds the first into the second', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.open(contradiction, 'amend', { side: 'other' });
    expect(page.amendSummary).toBe('Push hotfixes to main');
    page.amendSummary = 'Push hotfixes through a release branch';
    await page.submit(contradiction);
    expect(api.record).toHaveBeenLastCalledWith({ revise: { id: 'l-2', expected_version: 1, summary: 'Push hotfixes through a release branch' } });

    await page.sameAs(contradiction);
    expect(api.record).toHaveBeenLastCalledWith({ relate: { id: 'r-1', expected_version: 5, same_as: 'l-2' } });
  });
});
