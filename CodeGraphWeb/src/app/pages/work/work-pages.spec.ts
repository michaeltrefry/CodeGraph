import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { WorkApiService } from './work-api.service';
import { DependencyGraphComponent } from './work-dependency-graph.component';
import { fiftyItemFeature } from './work-fixtures';
import { WorkFeatureComponent } from './work-feature.component';
import { WorkItemComponent } from './work-item.component';
import { MessageListComponent } from './work-messages-ui';
import { WorkLiveService } from './work-live.service';
import { FeatureView, MessageView, WorkApiError, WorkChange } from './work.models';

const feature: FeatureView = {
  kind: 'feature',
  id: 'f',
  key: 'W-F1',
  project_id: 'p',
  project_key: 'W',
  title: 'Fixture feature',
  documents: [],
  version: 3,
  created_by: 'michael',
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  status: 'open'
};

/** Lets mocked promises settle (the pages load in a few awaits) and renders. */
async function settle(fixture: { detectChanges(): void }): Promise<void> {
  for (let round = 0; round < 3; round++) {
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

const fakeWatch = () => ({
  loading: signal(false),
  error: signal(''),
  onChange: () => () => undefined,
  stop: () => undefined
});

/** The streams the page opened, newest last: [project, onEvent]. */
const streams: { project: string; onEvent: (event: unknown) => void; stopped: boolean }[] = [];

function provideWork(api: Partial<Record<keyof WorkApiService, unknown>>, key: string, params = new BehaviorSubject(convertToParamMap({ key }))) {
  streams.length = 0;
  return [
    provideRouter([]),
    { provide: WorkApiService, useValue: api },
    {
      provide: WorkLiveService,
      useValue: {
        acquire: () => ({ watch: fakeWatch(), release: () => undefined }),
        streamMessages: (project: string, onEvent: (event: unknown) => void) => {
          const entry = { project, onEvent, stopped: false };
          streams.push(entry);
          return () => (entry.stopped = true);
        }
      }
    },
    { provide: ActivatedRoute, useValue: { snapshot: { paramMap: params.value }, paramMap: params } }
  ];
}

describe('DependencyGraphComponent with a 50-item feature', () => {
  async function render() {
    await TestBed.configureTestingModule({ imports: [DependencyGraphComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DependencyGraphComponent);
    fixture.componentRef.setInput('items', fiftyItemFeature());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('draws every item and shows blocked, cancelled-prerequisite and parallel items', async () => {
    const root = await render();
    expect(root.querySelectorAll('.wrk-g-node')).toHaveLength(50);
    expect(root.querySelectorAll('.wrk-g-edge')).toHaveLength(30);
    expect(root.querySelectorAll('.wrk-g-node[data-blocked="true"]')).toHaveLength(23);
    expect(root.querySelectorAll('.wrk-g-node[data-reason="cancelled_prerequisite"]')).toHaveLength(1);
    expect(root.querySelectorAll('.wrk-g-node[data-reason="awaiting_human"]')).toHaveLength(1);
    expect(root.querySelectorAll('.wrk-g-node[data-stuck="true"]')).toHaveLength(2);
    expect(root.querySelectorAll('.wrk-g-node.critical')).toHaveLength(14);
    expect(root.querySelectorAll('.wrk-g-edge.critical')).toHaveLength(13);
    expect(root.querySelectorAll('.wrk-g-edge.cancelled')).toHaveLength(1);
  });

  it('lists the critical path, the parallel sets and why each item is blocked', async () => {
    const root = await render();
    expect(root.querySelectorAll('.wrk-crit li')).toHaveLength(14);
    const sets = [...root.querySelectorAll('[data-testid="parallel-set"]')];
    expect(sets.length).toBe(14);
    expect(sets[0].textContent).toContain('Now');
    expect(sets[1].querySelectorAll('button')).toHaveLength(8);
    const blocked = [...root.querySelectorAll('[data-testid="blocked-item"]')].map(node => node.textContent ?? '');
    expect(blocked).toHaveLength(23);
    expect(blocked.some(text => text.includes('W-31') && text.includes('cancelled prerequisite'))).toBe(true);
    expect(blocked.some(text => text.includes('W-33') && text.includes('awaiting you'))).toBe(true);
    expect(blocked.some(text => text.includes('W-32') && text.includes('stuck behind a closed item'))).toBe(true);
  });

  it('names each node for assistive technology with its state', async () => {
    const root = await render();
    const label = root.querySelector('.wrk-g-node[data-key="W-31"]')!.getAttribute('aria-label')!;
    expect(label).toContain('cancelled prerequisite');
    expect(root.querySelector('.wrk-g-node[data-key="W-2"]')!.getAttribute('aria-label')).toContain('on the critical path');
  });
});

describe('WorkFeatureComponent board', () => {
  const items = fiftyItemFeature().filter(item => item.key === 'W-3' || item.key === 'W-33' || item.key === 'W-31');

  async function render(update: ReturnType<typeof vi.fn>) {
    const api = {
      get: vi.fn().mockResolvedValue(feature),
      items: vi.fn().mockResolvedValue(items),
      recentChanges: vi.fn().mockResolvedValue([]),
      update
    };
    await TestBed.configureTestingModule({ imports: [WorkFeatureComponent], providers: provideWork(api, 'W-F1') }).compileComponents();
    const fixture = TestBed.createComponent(WorkFeatureComponent);
    await settle(fixture);
    return { fixture, api, root: fixture.nativeElement as HTMLElement };
  }

  it('shows a column per status with the items in it', async () => {
    const { root } = await render(vi.fn());
    expect(root.querySelectorAll('.wrk-col')).toHaveLength(6);
    expect(root.querySelectorAll('.wrk-tile')).toHaveLength(3);
    expect(root.querySelector('.wrk-tile[data-reason="cancelled_prerequisite"]')).not.toBeNull();
  });

  it('moves an item with update_work and its version, as an agent would', async () => {
    const moved = { ...items[0], status: 'in_progress' as const, version: 2 };
    const update = vi.fn().mockResolvedValue({ item: moved });
    const { fixture, root } = await render(update);
    await fixture.componentInstance.move(items[0], 'in_progress');
    fixture.detectChanges();
    expect(update).toHaveBeenCalledWith('W-3', { expected_version: 1, status: 'in_progress' });
    expect(root.querySelector('.wrk-ok')?.textContent).toContain('W-3 moved to in progress');
  });

  it('shows a conflict clearly and reloads the current state', async () => {
    const update = vi.fn().mockRejectedValue(new WorkApiError(409, 'conflict', 'W-3 is at version 4, not 1.'));
    const { fixture, api, root } = await render(update);
    await fixture.componentInstance.move(items[0], 'in_review');
    fixture.detectChanges();
    const alert = root.querySelector('.wrk-conflict')!;
    expect(alert.textContent).toContain('Conflict');
    expect(alert.textContent).toContain('version 4');
    expect(api.items).toHaveBeenCalledTimes(2);
  });

  it('moves an item when it is dropped on a column', async () => {
    const update = vi.fn().mockResolvedValue({ item: { ...items[0], status: 'complete' } });
    const { fixture, root } = await render(update);
    const data = new Map<string, string>([['text/plain', 'W-3']]);
    const drop = new Event('drop', { bubbles: true, cancelable: true }) as Event & { dataTransfer: unknown };
    drop.dataTransfer = { getData: (type: string) => data.get(type) ?? '' };
    root.querySelector('.wrk-col[aria-label="complete column"]')!.dispatchEvent(drop);
    await settle(fixture);
    expect(update).toHaveBeenCalledWith('W-3', { expected_version: 1, status: 'complete' });
  });

  it('filters by claimed-by', async () => {
    const all = fiftyItemFeature();
    const api = { get: vi.fn().mockResolvedValue(feature), items: vi.fn().mockResolvedValue(all), recentChanges: vi.fn().mockResolvedValue([]), update: vi.fn() };
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({ imports: [WorkFeatureComponent], providers: provideWork(api, 'W-F1') }).compileComponents();
    const fixture = TestBed.createComponent(WorkFeatureComponent);
    await settle(fixture);
    fixture.componentInstance.fClaimed = 'claude';
    fixture.componentInstance.filtersChanged();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.wrk-tile')).toHaveLength(1);
  });
});

describe('WorkItemComponent', () => {
  it('shows who did what on the timeline, and GitHub reviews', async () => {
    const item = fiftyItemFeature()[1];
    item.comments = [{ id: 'c', author: 'michael', session_id: 's1', harness: 'claude-code', body: 'Started on the route', created_at: '2026-01-02T10:00:00Z' }];
    item.acceptance_criteria = [{ id: 'A1', text: 'Shows blocked items', state: 'unmet' }];
    const thread: MessageView = { id: 'm', project_key: 'W', channel: 'item', kind: 'question', from: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac' }, body: 'Which branch?', created_at: '2026-01-02T11:00:00Z', state: 'open', item: item.key };
    const entries: WorkChange[] = [{ sequence: 1, project_id: 'p', project_key: 'W', entity: 'item', entity_id: item.id, entity_key: item.key, kind: 'item_updated', actor: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac' }, changes: [{ field: 'status', old: 'ready', new: 'in_progress' }], created_at: '2026-01-02T09:00:00Z' }];
    const api = { get: vi.fn().mockResolvedValue(item), messages: vi.fn().mockResolvedValue({ items: [thread] }), itemChanges: vi.fn().mockResolvedValue(entries), githubEvidence: vi.fn().mockResolvedValue([{ id: 'e0', kind: 'push', repository: 'codegraph', url: 'https://github.com/o/r/commit/abcdef1234', author: 'claude-agent', source: 'human', priority: 'normal', superseded: false, external_id: 'abcdef1234567', body: 'abcdef1234567 Add the board', created_at: '2026-01-02T12:00:00Z' }, { id: 'e1', kind: 'review', repository: 'codegraph', pull_request: 9, url: 'https://github.com/o/r/pull/9', author: 'github:reviewer', source: 'human', priority: 'normal', review_state: 'approved', superseded: false, created_at: '2026-01-02T13:00:00Z' }]), update: vi.fn() };
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: provideWork(api, item.key) }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const timeline = root.querySelector('[data-testid="timeline"]')!;
    expect(timeline.querySelectorAll('li').length).toBeGreaterThanOrEqual(3);
    expect(timeline.textContent).toContain('claude-code / opus @ mac');
    expect(timeline.textContent).toContain('Status ready → in_progress');
    expect(root.textContent).toContain('Shows blocked items');
    expect(root.querySelector('[data-testid="evidence"]')?.textContent).toContain('github:reviewer');
    expect(root.querySelector('[data-testid="evidence"]')?.textContent).toContain('approved');
    expect(timeline.textContent).toContain('Review #9 (codegraph): approved');
    expect(root.querySelector('[data-testid="commits"]')?.textContent).toContain('abcdef1 Add the board');
    expect(root.querySelector('[data-testid="evidence"]')?.textContent).toContain('claude-agent');
    expect(timeline.textContent).toContain('Pushed 1 commit (head abcdef1)');
    expect(api.get).toHaveBeenCalledWith(item.key, 'full');
  });

  it('writes with the version it read, and surfaces a refusal', async () => {
    const item = fiftyItemFeature()[1];
    const update = vi.fn().mockRejectedValue(new WorkApiError(409, 'conflict', 'stale'));
    const api = { get: vi.fn().mockResolvedValue(item), messages: vi.fn().mockResolvedValue({ items: [] }), itemChanges: vi.fn().mockResolvedValue([]), githubEvidence: vi.fn().mockResolvedValue([]), update };
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: provideWork(api, item.key) }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    await settle(fixture);
    fixture.componentInstance.commentText = 'hello';
    await fixture.componentInstance.comment();
    fixture.detectChanges();
    expect(update).toHaveBeenCalledWith(item.key, { expected_version: 1, comment: 'hello' });
    expect(fixture.nativeElement.querySelector('.wrk-conflict')?.textContent).toContain('stale');
  });
});

describe('MessageListComponent', () => {
  it('answers an open question with in_reply_to and resolves, and declines with a reason', async () => {
    const post = vi.fn().mockResolvedValue({ created: true });
    const message: MessageView = { id: 'q1', project_key: 'W', channel: 'item', kind: 'question', from: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code' }, body: 'May I rebase?', created_at: '2026-01-02T11:00:00Z', state: 'open', blocking: true, item: 'W-7' };
    await TestBed.configureTestingModule({ imports: [MessageListComponent], providers: [provideRouter([]), { provide: WorkApiService, useValue: { post, ack: vi.fn() } }, { provide: WorkLiveService, useValue: {} }] }).compileComponents();
    const fixture = TestBed.createComponent(MessageListComponent);
    fixture.componentRef.setInput('messages', [message]);
    fixture.componentRef.setInput('project', 'W');
    fixture.detectChanges();
    const component = fixture.componentInstance;
    expect(fixture.nativeElement.textContent).toContain('blocks W-7');
    expect(fixture.nativeElement.textContent).toContain('claude-code');

    component.open(message, 'answered');
    component.reply = 'Yes, rebase.';
    await component.send(message);
    expect(post).toHaveBeenLastCalledWith(expect.objectContaining({ project: 'W', item: 'W-7', in_reply_to: 'q1', resolves: 'answered', body: 'Yes, rebase.' }));

    component.open(message, 'declined');
    component.reply = 'Not now.';
    await component.send(message);
    expect(post).toHaveBeenLastCalledWith(expect.objectContaining({ resolves: 'declined', body: 'Not now.' }));
  });
});
