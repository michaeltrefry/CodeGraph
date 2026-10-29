import { signal } from '@angular/core';
import { of } from 'rxjs';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { WorkApiService } from './work-api.service';
import { fiftyItemFeature } from './work-fixtures';
import { WorkLiveService } from './work-live.service';
import { WorkMessagesComponent } from './work-messages.component';
import { WorkNowComponent } from './work-now.component';
import { WorkProjectComponent } from './work-project.component';
import { FeatureView, HoldView, MessageView, ProjectView, ResourceView, SessionView, WorkChange } from './work.models';

const project: ProjectView = {
  kind: 'project', id: 'p', key: 'W', name: 'Workbench', goals: ['Ship the UI'], repositories: [{ repository: 'codegraph', role: 'owned' }],
  version: 2, created_by: 'michael', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', item_counts: { ready: 3, complete: 2 }
};
const session: SessionView = { id: 's1', project_key: 'W', identity: 'michael', harness: 'claude-code', model: 'opus', host: 'mac-studio', repository: 'codegraph', branch: 'claude/x', intent: 'Build the board', live: true, started_at: '2026-01-02T00:00:00Z', last_seen_at: '2026-01-02T00:01:00Z' };
const holder = { identity: 'michael', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac-studio' };
const hold = (over: Partial<HoldView>): HoldView => ({ id: 'h1', resource: 'gpu-0', project: 'W', holder, purpose: 'training run', state: 'held', priority: 'medium', ttl_seconds: 600, requested_at: '2026-01-02T00:00:00Z', expires_at: '2099-01-02T00:10:00Z', ...over });
const resource: ResourceView = { id: 'r', project: 'W', name: 'gpu-0', capacity: 1, labels: ['cuda'], used: 1, available: false, holders: [hold({})], waiters: [hold({ id: 'h2', state: 'waiting', queue_position: 1, purpose: 'eval', holder: { identity: 'alice' } })] };
const question: MessageView = { id: 'q', project_key: 'W', channel: 'item', kind: 'question', from: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac-studio' }, body: 'Which key should I use?', created_at: '2026-01-02T00:02:00Z', state: 'open', blocking: true, to_human: true, item: 'W-3' };
const change: WorkChange = { sequence: 9, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'i', entity_key: 'W-3', kind: 'item_updated', actor: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac-studio' }, changes: [{ field: 'status', old: 'ready', new: 'in_progress' }], created_at: '2026-01-02T00:03:00Z' };

const fakeWatch = () => ({ loading: signal(false), error: signal(''), onChange: () => () => undefined, stop: () => undefined });

async function settle(fixture: { detectChanges(): void }): Promise<void> {
  for (let round = 0; round < 4; round++) {
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

function providers(api: object, key = '') {
  return [
    provideRouter([]),
    { provide: WorkApiService, useValue: api },
    { provide: WorkLiveService, useValue: { acquire: () => ({ watch: fakeWatch(), release: () => undefined }), streamMessages: vi.fn(() => () => undefined) } },
    { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ key }) }, paramMap: of(convertToParamMap({ key })) } }
  ];
}

describe('WorkNowComponent', () => {
  it('shows live sessions, holds and waiters, what waits on you, and who did what', async () => {
    localStorage.setItem('codegraph.work.project', 'W');
    const held = { ...fiftyItemFeature()[1], claim: { holder: 'michael', marker: 'ui', claimed_at: '2026-01-02T00:00:00Z', expires_at: '2026-01-02T01:00:00Z', session_id: 's1' } };
    const api = {
      projects: vi.fn().mockResolvedValue([project]),
      sessions: vi.fn().mockResolvedValue([session]),
      resources: vi.fn().mockResolvedValue([resource]),
      messages: vi.fn().mockResolvedValue({ items: [question] }),
      items: vi.fn().mockResolvedValue([held]),
      recentChanges: vi.fn().mockResolvedValue([change])
    };
    await TestBed.configureTestingModule({ imports: [WorkNowComponent], providers: providers(api) }).compileComponents();
    const fixture = TestBed.createComponent(WorkNowComponent);
    fixture.detectChanges();
    await settle(fixture);
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(api.messages).toHaveBeenCalledWith('W', { open: true, toHuman: true, limit: 50 });
    expect(text).toContain('Blocked on you');
    expect(text).toContain('Which key should I use?');
    expect(text).toContain('blocks W-3');
    expect(text).toContain('claude-code / opus @ mac-studio');
    expect(text).toContain('Build the board');
    expect(text).toContain('holds');
    expect(text).toContain('gpu-0');
    expect(text).toContain('waiting #1');
    expect(text).toContain('training run');
    expect(text).toContain('last: updated');
    expect(text).toContain('status → in_progress');
  });
});

describe('WorkProjectComponent', () => {
  it('shows goals, member repositories, features with progress and recent activity', async () => {
    const feature: FeatureView = {
      kind: 'feature', id: 'f', key: 'W-F1', project_id: 'p', project_key: 'W', title: 'Board', documents: [], version: 1, created_by: 'michael',
      created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', status: 'open', item_counts: { complete: 2, ready: 1, in_progress: 1 }
    };
    const api = {
      get: vi.fn().mockResolvedValue(project),
      features: vi.fn().mockResolvedValue([feature]),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      recentChanges: vi.fn().mockResolvedValue([change]),
      recall: vi.fn().mockResolvedValue({ items: [{ id: 'k', kind: 'decision', summary: 'Use long-poll', confidence: 'high', version: 1 }] })
    };
    await TestBed.configureTestingModule({ imports: [WorkProjectComponent], providers: providers(api, 'W') }).compileComponents();
    const fixture = TestBed.createComponent(WorkProjectComponent);
    fixture.detectChanges();
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.textContent).toContain('Ship the UI');
    expect(root.textContent).toContain('codegraph');
    expect(root.textContent).toContain('W-F1');
    expect(root.textContent).toContain('2 / 4');
    expect(root.querySelector('.wrk-progress')?.getAttribute('aria-label')).toBe('50% finished');
    expect(root.textContent).toContain('Use long-poll');
    expect(root.textContent).toContain('claude-code / opus @ mac-studio');
  });
});

describe('WorkMessagesComponent', () => {
  it('lists channel messages with their senders and the human inbox', async () => {
    localStorage.setItem('codegraph.work.project', 'W');
    const api = {
      projects: vi.fn().mockResolvedValue([project]),
      messages: vi.fn().mockResolvedValue({ items: [question], next_before: '5' }),
      inbox: vi.fn().mockResolvedValue({ items: [question], next_cursor: 'c' })
    };
    await TestBed.configureTestingModule({ imports: [WorkMessagesComponent], providers: providers(api) }).compileComponents();
    const fixture = TestBed.createComponent(WorkMessagesComponent);
    fixture.detectChanges();
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('.wrk-msg').length).toBe(2);
    expect(root.textContent).toContain('Older messages');
    expect(root.textContent).toContain('Human inbox');
    expect(root.textContent).toContain('claude-code / opus @ mac-studio');
  });
});
