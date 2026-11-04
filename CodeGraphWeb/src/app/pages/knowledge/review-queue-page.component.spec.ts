import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeApiService } from './knowledge-api.service';
import { ReviewQueuePageComponent } from './review-queue-page.component';
import { HUMAN, challenge, entry, reviewItem } from './knowledge-fixtures';
import { ReviewItem } from './knowledge.models';

describe('ReviewQueuePageComponent', () => {
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
  let api: { [K in 'listProjects' | 'reviewQueue' | 'record' | 'promote' | 'challenge']: ReturnType<typeof vi.fn> };

  async function create(items: ReviewItem[] = [challenged, contradiction]) {
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
      record: vi.fn().mockReturnValue(of({ created: false, entry: entry({ id: 'l-2', version: 2 }) })),
      promote: vi.fn().mockReturnValue(of({ entry: entry(), changed: true })),
      challenge: vi.fn().mockReturnValue(of({ created: false, challenge: challenge({ status: 'upheld' }), rule: entry() }))
    };
  });

  it('lists open challenges and contradictions with their actors, and says nothing waits for approval', async () => {
    const fixture = await create();
    expect(api.reviewQueue).toHaveBeenCalledWith('CG', 20, undefined);
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Nothing here blocks work, and nothing waits for your approval');
    for (const label of ['Challenged rule', 'Contradicting lessons']) expect(text).toContain(label);
    for (const label of ['Proposed rule', 'Autonomous decision']) expect(text).not.toContain(label);
    expect(text).toContain('Release branches need it');
    expect(text).toContain('Push hotfixes to main');
    expect(text).toContain('michael');
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
    api.record.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Version 3 is current.' } })));
    await fixture.componentInstance.sameAs(contradiction);
    expect(fixture.componentInstance.error()).toContain('reloaded');
    expect(api.reviewQueue).toHaveBeenCalledTimes(2);
  });

  it('amends the second lesson of a contradiction and folds the first into the second', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.open(contradiction, 'amend', { side: 'other' });
    expect(page.amendSummary).toBe('Push hotfixes to main');
    page.amendSummary = 'Push hotfixes through a release branch';
    await page.submit(contradiction);
    expect(api.record).toHaveBeenLastCalledWith({ revise: { id: 'l-2', expected_version: 1, summary: 'Push hotfixes through a release branch' } });
    expect(page.notice()).toBe('Amended.');

    await page.sameAs(contradiction);
    expect(api.record).toHaveBeenLastCalledWith({ relate: { id: 'r-1', expected_version: 5, same_as: 'l-2' } });
  });
});

describe('ReviewQueuePageComponent embedded in a project page', () => {
  it('renders the approvals heading without the page header or project select, for the input project, and reloads when it changes', async () => {
    TestBed.resetTestingModule();
    localStorage.removeItem('codegraph.knowledge.project');
    const api = { listProjects: vi.fn().mockReturnValue(of([])), reviewQueue: vi.fn().mockReturnValue(of({ items: [reviewItem()] })) };
    await TestBed.configureTestingModule({ imports: [ReviewQueuePageComponent], providers: [{ provide: KnowledgeApiService, useValue: api }] }).compileComponents();
    const fixture = TestBed.createComponent(ReviewQueuePageComponent);
    fixture.componentRef.setInput('project', 'ONE');
    const settle = async () => { fixture.detectChanges(); for (let i = 0; i < 5; i++) await new Promise(resolve => setTimeout(resolve)); fixture.detectChanges(); };
    await settle();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.cg-page-header')).toBeNull();
    expect(el.querySelector('select')).toBeNull();
    expect(el.querySelector('h2')?.textContent).toBe('Knowledge approvals');
    expect(el.textContent).toContain('What an agent cannot resolve alone, by priority then recurrence');
    expect(el.textContent).toContain('Squash fixups before review');
    expect(api.listProjects).not.toHaveBeenCalled();
    expect(api.reviewQueue).toHaveBeenCalledTimes(1);
    expect(api.reviewQueue).toHaveBeenLastCalledWith('ONE', 20, undefined);

    fixture.componentRef.setInput('project', 'TWO');
    await settle();
    expect(api.reviewQueue).toHaveBeenCalledTimes(2);
    expect(api.reviewQueue).toHaveBeenLastCalledWith('TWO', 20, undefined);
    expect(localStorage.getItem('codegraph.knowledge.project')).toBeNull();
  });
});
