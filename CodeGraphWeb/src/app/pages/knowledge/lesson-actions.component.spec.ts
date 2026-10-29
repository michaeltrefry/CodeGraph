import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeApiService } from './knowledge-api.service';
import { LessonActionsComponent } from './lesson-actions.component';
import { challenge, rule } from './knowledge-fixtures';
import { KnowledgeEntry } from './knowledge.models';

describe('LessonActionsComponent', () => {
  let api: { [K in 'getEntry' | 'record' | 'promote' | 'challenge']: ReturnType<typeof vi.fn> };
  let emitted: KnowledgeEntry[];

  async function create(lesson: KnowledgeEntry = rule()) {
    await TestBed.configureTestingModule({
      imports: [LessonActionsComponent],
      providers: [{ provide: KnowledgeApiService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(LessonActionsComponent);
    fixture.componentRef.setInput('entry', lesson);
    fixture.componentInstance.changed.subscribe(entry => emitted.push(entry));
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    emitted = [];
    const after = rule({ version: 6 });
    api = {
      getEntry: vi.fn().mockReturnValue(of(rule({ version: 9 }))),
      record: vi.fn().mockReturnValue(of({ created: false, entry: after })),
      promote: vi.fn().mockReturnValue(of({ entry: after, changed: true })),
      challenge: vi.fn().mockReturnValue(of({ created: true, challenge: challenge(), rule: after }))
    };
  });

  it('renders the tier, recurrence and open challenges with their authors', async () => {
    const fixture = await create();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('rule · from human prompt');
    expect(text).toContain('2 events · 1 while served');
    expect(text).toContain('Release branches need it');
    expect(text).toContain('claude-code / opus @ dev-mac');
  });

  it('retracts a rule with a reason and the version it read', async () => {
    const fixture = await create();
    const actions = fixture.componentInstance;
    actions.open('retract');
    await actions.retract();
    expect(actions.error()).toBe('Give a reason for retracting.');
    actions.reason = 'Too broad';
    await actions.retract();
    expect(api.record).toHaveBeenCalledWith({ retract: { id: 'r-1', expected_version: 5, reason: 'Too broad' } });
    expect(emitted.at(-1)?.version).toBe(6);
  });

  it('resolves a challenge: uphold, amend with the new text, retract', async () => {
    const fixture = await create();
    const actions = fixture.componentInstance;

    actions.openResolve(challenge(), 'amend');
    actions.amendSummary = 'Never push to main outside a release';
    actions.reason = 'Releases are the exception';
    await actions.resolve();
    expect(api.challenge).toHaveBeenLastCalledWith({
      resolve: { challenge_id: 'c-1', expected_version: 5, outcome: 'amend', reason: 'Releases are the exception', amend: { summary: 'Never push to main outside a release' } }
    });

    actions.openResolve(challenge(), 'uphold');
    actions.reason = 'Still right';
    await actions.resolve();
    expect(api.challenge).toHaveBeenLastCalledWith({ resolve: { challenge_id: 'c-1', expected_version: 5, outcome: 'uphold', reason: 'Still right' } });
  });

  it('opens a challenge with evidence, reusing its idempotency key on retry and resetting it after success', async () => {
    const fixture = await create();
    const actions = fixture.componentInstance;
    actions.open('challenge');
    actions.reason = 'Hotfixes';
    actions.evidenceSummary = 'HOTFIX.md says push';
    api.challenge.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503, error: { code: 'busy', message: 'Busy.' } })));

    await actions.openChallenge();
    const key = api.challenge.mock.calls[0][0].idempotency_key;
    await actions.openChallenge();

    expect(api.challenge.mock.calls[1][0]).toEqual({
      idempotency_key: key,
      open: { id: 'r-1', reason: 'Hotfixes', evidence: [{ kind: 'counter_example', summary: 'HOTFIX.md says push' }] }
    });
    expect(actions.challengeKey.value).not.toBe(key);
  });

  it('proposes, approves, rejects with a reason and marks addressed_by', async () => {
    const fixture = await create(rule({ lesson: { ...rule().lesson!, state: 'candidate', challenges: [] } }));
    const actions = fixture.componentInstance;

    await actions.propose();
    expect(api.promote).toHaveBeenLastCalledWith('r-1', { expected_version: 5, action: 'propose' });
    await actions.approve();
    expect(api.promote).toHaveBeenLastCalledWith('r-1', { expected_version: 5, action: 'approve' });

    await actions.reject();
    expect(actions.error()).toBe('Give the reason it should not be a rule.');
    actions.reason = 'Too noisy';
    await actions.reject();
    expect(api.promote).toHaveBeenLastCalledWith('r-1', { expected_version: 5, action: 'reject', reason: 'Too noisy' });

    actions.addressedKind = 'hook';
    actions.addressedName = 'pre-push';
    await actions.addressedBy();
    expect(actions.error()).toBe('An instruction or hook names its harness.');
    actions.addressedHarness = 'claude-code';
    actions.addressedRef = 'abc123';
    await actions.addressedBy();
    expect(api.promote).toHaveBeenLastCalledWith('r-1', {
      expected_version: 5, action: 'addressed_by', addressed_by: { kind: 'hook', name: 'pre-push', ref: 'abc123', harness: 'claude-code' }
    });
  });

  it('edits the text and applies-when, and relates to another lesson', async () => {
    const fixture = await create();
    const actions = fixture.componentInstance;
    actions.open('edit');
    actions.toggleActivity('releasing', true);
    await actions.edit();
    expect(api.record).toHaveBeenLastCalledWith({ revise: { id: 'r-1', expected_version: 5, applies_when: { activity: ['merging', 'releasing'] } } });

    actions.open('relate');
    actions.relateTo = 'l-9';
    await actions.relate();
    expect(api.record).toHaveBeenLastCalledWith({ relate: { id: 'r-1', expected_version: 5, contradicts: 'l-9' } });
  });

  it('reloads the lesson when a write is refused conflict', async () => {
    const fixture = await create();
    const actions = fixture.componentInstance;
    api.promote.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Version 9 is current.' } })));
    await actions.approve();
    expect(actions.error()).toContain('reloaded');
    expect(emitted.at(-1)?.version).toBe(9);
  });
});
