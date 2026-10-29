import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { WorkApiService } from './work-api.service';
import { fiftyItemFeature } from './work-fixtures';
import { WorkFeatureComponent } from './work-feature.component';
import { WorkItemComponent } from './work-item.component';
import { KnowledgeComponent } from './work-knowledge-ui';
import { WorkLiveService } from './work-live.service';
import { WorkMessagesComponent } from './work-messages.component';
import { WorkProjectComponent } from './work-project.component';
import { FeatureView, KnowledgeEntry, MessageView, ProjectView, WorkApiError } from './work.models';

async function settle(fixture: { detectChanges(): void }): Promise<void> {
  for (let round = 0; round < 4; round++) {
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

const streams: { project: string; onEvent: (event: unknown) => void; stopped: boolean }[] = [];

function providers(api: object, key: string, params = new BehaviorSubject(convertToParamMap({ key }))) {
  streams.length = 0;
  return [
    provideRouter([{ path: '**', children: [] }]),
    { provide: WorkApiService, useValue: api },
    {
      provide: WorkLiveService,
      useValue: {
        acquire: () => ({ watch: { loading: signal(false), error: signal(''), onChange: () => () => undefined, stop: () => undefined }, release: () => undefined }),
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

const project: ProjectView = {
  kind: 'project', id: 'p', key: 'W', name: 'Workbench', goals: [], repositories: [], version: 1, created_by: 'michael',
  created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z'
};

describe('route parameter reuse (B1)', () => {
  it('shows the created project after /work/projects/new navigates to it, and a retried create repeats its key', async () => {
    const create = vi.fn().mockRejectedValueOnce(new WorkApiError(0, 'network', 'offline')).mockResolvedValue({ created: true, project });
    const api = {
      create,
      projects: vi.fn().mockResolvedValue([project]),
      get: vi.fn().mockResolvedValue(project),
      features: vi.fn().mockResolvedValue([]),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      recentChanges: vi.fn().mockResolvedValue([]),
      recall: vi.fn().mockResolvedValue({ items: [] })
    };
    const params = new BehaviorSubject(convertToParamMap({ key: 'new' }));
    await TestBed.configureTestingModule({ imports: [WorkProjectComponent], providers: providers(api, 'new', params) }).compileComponents();
    const fixture = TestBed.createComponent(WorkProjectComponent);
    fixture.detectChanges();
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('New project');

    await fixture.componentInstance.createProject({ key: 'W', name: 'Workbench' });
    await fixture.componentInstance.createProject({ key: 'W', name: 'Workbench' });
    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[0][0].idempotency_key).toBeTruthy();
    expect(create.mock.calls[1][0].idempotency_key).toBe(create.mock.calls[0][0].idempotency_key);

    // The router reuses the component and only the parameter changes.
    params.next(convertToParamMap({ key: 'W' }));
    await settle(fixture);
    expect(fixture.componentInstance.creating()).toBe(false);
    expect(root.textContent).not.toContain('New project');
    expect(root.textContent).toContain('Workbench');
    expect(api.get).toHaveBeenCalledWith('W');
  });

  it('shows the linked item when the route key changes, and its live filter follows the new key', async () => {
    const items = fiftyItemFeature();
    const api = {
      get: vi.fn((key: string) => Promise.resolve(items.find(item => item.key === key))),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      itemChanges: vi.fn().mockResolvedValue([]),
      githubEvidence: vi.fn().mockResolvedValue([]),
      update: vi.fn()
    };
    const params = new BehaviorSubject(convertToParamMap({ key: 'W-10' }));
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: providers(api, 'W-10', params) }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    fixture.detectChanges();
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('h1')?.textContent).toContain('Item 10');
    expect(streams).toHaveLength(1);

    params.next(convertToParamMap({ key: 'W-2' }));
    await settle(fixture);
    expect(root.querySelector('h1')?.textContent).toContain('Item 2');
    expect(api.itemChanges).toHaveBeenLastCalledWith('W', 'W-2');
    expect(streams).toHaveLength(2);
    expect(streams[0].stopped).toBe(true);

    // A message on the old item no longer reloads the page; one on the new item does.
    api.get.mockClear();
    streams[1].onEvent({ message: { item: 'W-10' } });
    await new Promise(resolve => setTimeout(resolve, 450));
    expect(api.get).not.toHaveBeenCalled();
    streams[1].onEvent({ message: { item: 'W-2' } });
    await new Promise(resolve => setTimeout(resolve, 450));
    expect(api.get).toHaveBeenCalledWith('W-2', 'full');
  });

  it('matches thread events against the resolved key when the route names the item by alias', async () => {
    const item = fiftyItemFeature()[4];
    const api = {
      get: vi.fn().mockResolvedValue(item),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      itemChanges: vi.fn().mockResolvedValue([]),
      githubEvidence: vi.fn().mockResolvedValue([]),
      update: vi.fn()
    };
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: providers(api, 'sc-27874') }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    fixture.detectChanges();
    await settle(fixture);
    api.get.mockClear();
    streams[0].onEvent({ message: { item: item.key } });
    await new Promise(resolve => setTimeout(resolve, 450));
    expect(api.get).toHaveBeenCalledWith('sc-27874', 'full');
  });

  it('reads the item timeline from the item-filtered feed, not the project feed', async () => {
    const item = fiftyItemFeature()[1];
    const api = {
      get: vi.fn().mockResolvedValue(item),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      itemChanges: vi.fn().mockResolvedValue([{ sequence: 1, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'i', entity_key: item.key, kind: 'item_updated', actor: { name: 'michael', kind: 'user' }, changes: [{ field: 'status', old: 'ready', new: 'in_progress' }], created_at: '2026-01-02T09:00:00Z' }]),
      githubEvidence: vi.fn().mockResolvedValue([]),
      update: vi.fn()
    };
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: providers(api, item.key) }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    fixture.detectChanges();
    await settle(fixture);
    expect(api.itemChanges).toHaveBeenCalledWith('W', item.key);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="timeline"]')?.textContent).toContain('Status ready → in_progress');
  });
});

describe('item page writes (B2, should-fix 5, 6)', () => {
  async function render(update: ReturnType<typeof vi.fn>) {
    const item = { ...fiftyItemFeature()[1], status: 'in_progress' as const };
    const api = {
      get: vi.fn().mockResolvedValue(item),
      messages: vi.fn().mockResolvedValue({ items: [] }),
      itemChanges: vi.fn().mockResolvedValue([]),
      githubEvidence: vi.fn().mockResolvedValue([]),
      update
    };
    await TestBed.configureTestingModule({ imports: [WorkItemComponent], providers: providers(api, item.key) }).compileComponents();
    const fixture = TestBed.createComponent(WorkItemComponent);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, item, root: fixture.nativeElement as HTMLElement };
  }

  it('puts the status select back to the real status after a refused move', async () => {
    const { fixture, item, root } = await render(vi.fn().mockRejectedValue(new WorkApiError(409, 'conflict', 'stale')));
    const select = root.querySelector('select[aria-label="Status"]') as HTMLSelectElement;
    expect(select.value).toBe('in_progress');
    select.value = 'complete';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(select.value).toBe(item.status);
    expect(root.querySelector('.wrk-conflict')?.textContent).toContain('stale');
  });

  it('records save points through update_work with the version read', async () => {
    const update = vi.fn().mockResolvedValue({});
    const { fixture, item, root } = await render(update);
    const component = fixture.debugElement.query(el => el.name === 'wrk-save-points').componentInstance;
    component.decision = 'Use long-poll';
    component.why = 'EventSource cannot send headers';
    component.submit();
    await settle(fixture);
    expect(update).toHaveBeenLastCalledWith(item.key, { expected_version: 1, decisions: [{ decision: 'Use long-poll', why: 'EventSource cannot send headers' }] });

    component.kind = 'attempt';
    component.tried = 'EventSource';
    component.whyFailed = 'no auth header';
    component.submit();
    await settle(fixture);
    expect(update).toHaveBeenLastCalledWith(item.key, { expected_version: 1, attempts: [{ tried: 'EventSource', why_failed: 'no auth header' }] });

    component.kind = 'correction';
    component.what = 'Used clamp';
    component.correct = 'Refuse';
    component.source = 'review';
    component.submit();
    await settle(fixture);
    expect(update).toHaveBeenLastCalledWith(item.key, { expected_version: 1, corrections: [{ what: 'Used clamp', correct_behavior: 'Refuse', source: { kind: 'review' } }] });

    component.kind = 'finding';
    component.findingKind = 'gotcha';
    component.summary = 'Streams need fetch';
    component.submit();
    await settle(fixture);
    expect(update).toHaveBeenLastCalledWith(item.key, { expected_version: 1, findings: [{ kind: 'gotcha', summary: 'Streams need fetch' }] });
    expect(root.textContent).toContain('Save points');
  });

  it('keeps typed save-point text when the write is refused and clears it once it succeeds', async () => {
    const update = vi.fn().mockRejectedValueOnce(new WorkApiError(409, 'conflict', 'stale')).mockResolvedValue({});
    const { fixture } = await render(update);
    const component = fixture.debugElement.query(el => el.name === 'wrk-save-points').componentInstance;
    component.decision = 'Keep me';
    component.submit();
    await settle(fixture);
    expect(component.decision).toBe('Keep me');
    component.submit();
    await settle(fixture);
    expect(component.decision).toBe('');
  });

  it('sends order when an item is edited', async () => {
    const update = vi.fn().mockResolvedValue({});
    const { fixture, item } = await render(update);
    fixture.componentInstance.dialog.set('edit');
    fixture.detectChanges();
    await settle(fixture);
    const form = fixture.debugElement.query(el => el.name === 'wrk-item-form').componentInstance;
    form.order = 7;
    form.submit();
    await settle(fixture);
    expect(update).toHaveBeenLastCalledWith(item.key, { expected_version: 1, order: 7 });
  });
});

describe('feature documents and save points (B2)', () => {
  const feature: FeatureView = {
    kind: 'feature', id: 'f', key: 'W-F1', project_id: 'p', project_key: 'W', title: 'Work UI', version: 3, created_by: 'michael',
    created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', status: 'open',
    documents: [{ id: 'd1', title: 'Design', body: 'The plan', position: 0 }]
  };

  it('shows documents and adds, edits and saves points with update_work', async () => {
    const update = vi.fn().mockResolvedValue({});
    const api = { get: vi.fn().mockResolvedValue(feature), items: vi.fn().mockResolvedValue([]), recentChanges: vi.fn().mockResolvedValue([]), update };
    await TestBed.configureTestingModule({ imports: [WorkFeatureComponent], providers: providers(api, 'W-F1') }).compileComponents();
    const fixture = TestBed.createComponent(WorkFeatureComponent);
    fixture.detectChanges();
    await settle(fixture);
    const component = fixture.componentInstance;
    component.tab.set('docs');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="document"]')?.textContent).toContain('The plan');

    component.editDocument(feature.documents[0]);
    component.docBody = 'The better plan';
    await component.saveDocument('d1');
    expect(update).toHaveBeenLastCalledWith('W-F1', { expected_version: 3, update_documents: [{ id: 'd1', body: 'The better plan' }] });

    component.newDocTitle = 'Notes';
    component.newDocBody = 'Body';
    await component.addDocument();
    expect(update).toHaveBeenLastCalledWith('W-F1', { expected_version: 3, add_documents: [{ title: 'Notes', body: 'Body' }] });

    await component.savePoint({ findings: [{ kind: 'fact', summary: 'x' }] });
    expect(update).toHaveBeenLastCalledWith('W-F1', { expected_version: 3, findings: [{ kind: 'fact', summary: 'x' }] });
  });

  it('keeps one idempotency key for a create across a retry and renews it after success', async () => {
    const create = vi.fn().mockRejectedValueOnce(new WorkApiError(0, 'network', 'offline')).mockResolvedValue({ created: true });
    const api = { get: vi.fn().mockResolvedValue(feature), items: vi.fn().mockResolvedValue([]), recentChanges: vi.fn().mockResolvedValue([]), create };
    await TestBed.configureTestingModule({ imports: [WorkFeatureComponent], providers: providers(api, 'W-F1') }).compileComponents();
    const fixture = TestBed.createComponent(WorkFeatureComponent);
    fixture.detectChanges();
    await settle(fixture);
    await fixture.componentInstance.createItem({ title: 'A' });
    await fixture.componentInstance.createItem({ title: 'A' });
    await fixture.componentInstance.createItem({ title: 'B' });
    const keys = create.mock.calls.map(call => call[0].idempotency_key);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it('says so when a retried create finds the earlier attempt already made it', async () => {
    const create = vi.fn().mockResolvedValue({ created: false });
    const api = { get: vi.fn().mockResolvedValue(feature), items: vi.fn().mockResolvedValue([]), recentChanges: vi.fn().mockResolvedValue([]), create };
    await TestBed.configureTestingModule({ imports: [WorkFeatureComponent], providers: providers(api, 'W-F1') }).compileComponents();
    const fixture = TestBed.createComponent(WorkFeatureComponent);
    fixture.detectChanges();
    await settle(fixture);
    await fixture.componentInstance.createItem({ title: 'A' });
    fixture.detectChanges();
    expect(fixture.componentInstance.notice()).toBe("Already created by an earlier attempt; your later edits weren't applied.");
  });
});

describe('knowledge (S5 actions and actors)', () => {
  const entry: KnowledgeEntry = {
    id: 'k1', kind: 'decision', summary: 'Use long-poll', confidence: 'high', version: 4, revision: 2, body: 'Because',
    author: { name: 'michael', kind: 'session', session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac' }
  };

  async function render(api: object) {
    await TestBed.configureTestingModule({ imports: [KnowledgeComponent], providers: providers(api, 'W') }).compileComponents();
    const fixture = TestBed.createComponent(KnowledgeComponent);
    fixture.componentRef.setInput('project', 'W');
    fixture.detectChanges();
    await settle(fixture);
    return fixture;
  }

  it('shows who wrote each entry and revises with the version read', async () => {
    const record = vi.fn().mockResolvedValue({});
    const recall = vi.fn().mockResolvedValue({ items: [entry] });
    const fixture = await render({ recall, record });
    expect(recall).toHaveBeenCalledWith(expect.objectContaining({ project: 'W', detail: 'full', limit: 10 }));
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="knowledge"]')?.textContent).toContain('claude-code / opus @ mac');

    fixture.componentInstance.startRevise(entry);
    fixture.componentInstance.reviseSummary = 'Use long-poll everywhere';
    await fixture.componentInstance.revise(entry);
    expect(record).toHaveBeenLastCalledWith({ project: 'W', revise: { id: 'k1', expected_version: 4, summary: 'Use long-poll everywhere' } });
  });

  it('imports knowledge with a dry run, then confirms the accepted indexes with the preview token', async () => {
    const importKnowledge = vi.fn()
      .mockResolvedValueOnce({ stored: false, preview_token: 'tok', candidates: [{ index: 0, outcome: 'would_store' }, { index: 1, outcome: 'duplicate', message: 'same as k1' }] })
      .mockResolvedValueOnce({ stored: true, preview_token: 'tok', candidates: [{ index: 0, outcome: 'stored' }, { index: 1, outcome: 'duplicate' }] });
    const fixture = await render({ recall: vi.fn().mockResolvedValue({ items: [] }), importKnowledge });
    const component = fixture.componentInstance;
    const candidates = [{ entry: { kind: 'fact', summary: 'A' }, source_file: 'a.md', verification: 'verified' }, { entry: { kind: 'fact', summary: 'B' }, source_file: 'b.md', verification: 'verified' }];
    component.importJson = JSON.stringify(candidates);
    await component.previewImport();
    fixture.detectChanges();
    expect(importKnowledge).toHaveBeenLastCalledWith({ project: 'W', candidates });
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="import-preview"]')?.textContent).toContain('same as k1');
    expect([...component.accepted()]).toEqual([0]);

    await component.confirmImport(component.importPreview()!);
    expect(importKnowledge).toHaveBeenLastCalledWith(expect.objectContaining({ project: 'W', candidates, confirm: true, accept: [0], preview_token: 'tok', idempotency_key: expect.any(String) }));
  });

  it('refuses a candidates box that is not a JSON array', async () => {
    const fixture = await render({ recall: vi.fn().mockResolvedValue({ items: [] }), importKnowledge: vi.fn() });
    fixture.componentInstance.importJson = '{"a":1}';
    await fixture.componentInstance.previewImport();
    expect(fixture.componentInstance.importError()).toContain('JSON array');
  });
});

describe('message board refresh (should-fix 7)', () => {
  const message = (id: string): MessageView => ({ id, project_key: 'W', channel: 'project', kind: 'info', from: { name: 'michael', kind: 'user' }, body: id, created_at: '2026-01-02T00:00:00Z' });

  it('merges new messages at the head and keeps the older pages already loaded', async () => {
    localStorage.setItem('codegraph.work.project', 'W');
    const messages = vi.fn()
      .mockResolvedValueOnce({ items: [message('m3'), message('m2')], next_before: '2' })
      .mockResolvedValueOnce({ items: [message('m1')] })
      .mockResolvedValueOnce({ items: [message('m4'), message('m3')], next_before: '3' });
    const api = { projects: vi.fn().mockResolvedValue([project]), messages, inbox: vi.fn().mockResolvedValue({ items: [], next_cursor: 'c' }) };
    await TestBed.configureTestingModule({ imports: [WorkMessagesComponent], providers: providers(api, '') }).compileComponents();
    const fixture = TestBed.createComponent(WorkMessagesComponent);
    fixture.detectChanges();
    await settle(fixture);
    const component = fixture.componentInstance;
    await component.loadMore();
    expect(component.messages().map(m => m.id)).toEqual(['m3', 'm2', 'm1']);

    // A stream event reloads the first page only; the older pages stay.
    await component.load(false);
    expect(component.messages().map(m => m.id)).toEqual(['m4', 'm3', 'm2', 'm1']);
  });
});
