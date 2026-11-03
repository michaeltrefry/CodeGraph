import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { WorkApiService } from '../work/work-api.service';
import { WorkLiveService } from '../work/work-live.service';
import { MessageView, ProjectView, ResourceView, SessionView, WorkChange } from '../work/work.models';
import { featureSet, project, settle } from './project-fixtures';
import { ProjectWorkComponent } from './project-work.component';

const session: SessionView = { id: 's1', project_key: 'W', identity: 'michael', harness: 'claude-code', model: 'opus', host: 'mac', intent: 'Build the tab', live: true, started_at: '2026-01-02T00:00:00Z', last_seen_at: '2026-01-02T00:01:00Z' };
const resource: ResourceView = {
  id: 'r', project: 'W', name: 'gpu-0', capacity: 1, labels: [], used: 1, available: false,
  holders: [{ id: 'h1', resource: 'gpu-0', project: 'W', holder: { identity: 'michael' }, purpose: 'training run', state: 'held', priority: 'medium', ttl_seconds: 600, requested_at: '2026-01-02T00:00:00Z', expires_at: '2099-01-02T00:10:00Z' }]
};
const question: MessageView = { id: 'q', project_key: 'W', channel: 'item', kind: 'question', from: { name: 'michael', kind: 'session', session_id: 's1' }, body: 'Which key should I use?', created_at: '2026-01-02T00:02:00Z', state: 'open', blocking: true, to_human: true, item: 'W-3' };
const changes: WorkChange[] = Array.from({ length: 20 }, (_, index) => ({
  sequence: index + 1, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'i', entity_key: `W-${index + 1}`, kind: 'item_updated',
  actor: { name: 'michael', kind: 'user' }, changes: [], created_at: '2026-01-02T00:03:00Z'
}));

@Component({ standalone: true, imports: [ProjectWorkComponent], template: '<div class="wrk-page"><app-project-work [project]="project()" [revision]="revision()" /></div>' })
class HostComponent {
  readonly project = signal<ProjectView>(project);
  readonly revision = signal(1);
}

async function render(query: Record<string, string> = {}, over: Record<string, unknown> = {}) {
  const queryParams = new BehaviorSubject(convertToParamMap(query));
  const api = {
    features: vi.fn().mockResolvedValue(featureSet()),
    sessions: vi.fn().mockResolvedValue([session]),
    resources: vi.fn().mockResolvedValue([resource]),
    messages: vi.fn().mockResolvedValue({ items: [question] }),
    items: vi.fn().mockResolvedValue([]),
    recentChanges: vi.fn().mockResolvedValue(changes),
    create: vi.fn().mockResolvedValue({ created: true }),
    ...over
  };
  const streamMessages = vi.fn(() => () => undefined);
  await TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [
      provideRouter([{ path: '**', children: [] }]),
      { provide: WorkApiService, useValue: api },
      { provide: WorkLiveService, useValue: { acquire: vi.fn(), streamMessages } },
      { provide: ActivatedRoute, useValue: { queryParamMap: queryParams, snapshot: { queryParamMap: queryParams.value } } }
    ]
  }).compileComponents();
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(async (_commands, extras) => {
    queryParams.next(convertToParamMap(extras?.queryParams ?? {}));
    return true;
  });
  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  await settle(fixture);
  const root = fixture.nativeElement as HTMLElement;
  const rows = () => [...root.querySelectorAll('.prj-ftable tbody tr')].map(row => row.getAttribute('data-key'));
  const click = async (element: Element | null | undefined) => {
    (element as HTMLElement).click();
    await settle(fixture);
  };
  return { fixture, root, api, navigate, queryParams, streamMessages, rows, click };
}

describe('ProjectWorkComponent', () => {
  it('shows Blocked on you, Live sessions, Features, Holds and waiters and Recent activity for the project', async () => {
    const { root, api, streamMessages } = await render();
    const headings = [...root.querySelectorAll('h2')].map(h2 => h2.textContent?.replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Blocked on you 1', 'Features', 'Holds and waiters', 'Recent activity', 'Live sessions 1']);
    // Live sessions is the sidebar; Features follows Blocked on you in the main column.
    expect(root.querySelector('.prj-work-side h2')?.textContent?.trim()).toBe('Live sessions 1');
    expect([...root.querySelectorAll('.prj-work-main > section h2')].map(h => h.id)).toEqual(['prj-h-you', 'prj-h-features']);
    expect(api.features).toHaveBeenCalledWith('W');
    expect(api.messages).toHaveBeenCalledWith('W', { open: true, toHuman: true, limit: 50 });
    expect(streamMessages).toHaveBeenCalledWith('W', expect.any(Function), undefined, expect.any(Function));
    expect(root.querySelector('.wrk-attention')?.textContent).toContain('Which key should I use?');
    expect(root.textContent).toContain('Build the tab');
    expect(root.textContent).toContain('gpu-0');
    // Recent activity is limited to 15 entries.
    expect(root.querySelectorAll('[aria-labelledby="prj-h-activity"] .wrk-list > li')).toHaveLength(15);
  });

  it('renders the features table with flags, status, progress, remaining and the count', async () => {
    const { root, rows } = await render();
    expect(rows()).toEqual(['W-F2', 'W-F1', 'W-F3', 'W-F10', 'W-F4']);
    expect(root.querySelector('.prj-shown')?.textContent).toBe('5 features');
    const search = root.querySelector('tr[data-key="W-F2"]')!;
    expect(search.querySelector('.wrk-key')?.textContent).toBe('W-F2');
    expect(search.querySelector('a')?.getAttribute('href')).toBe('/work/features/W-F2');
    expect(search.querySelector('[data-flag="uncovered"]')?.textContent).toBe('2 uncovered requirements');
    expect(search.querySelector('[data-flag="uncovered"]')?.classList).toContain('cg-chip-warn');
    expect(search.querySelector('[data-flag="blocked"]')?.textContent).toBe('2 blocked');
    expect(search.querySelector('.wrk-chip[data-status]')?.getAttribute('data-status')).toBe('in_progress');
    expect(search.querySelector('.prj-pct')?.textContent).toBe('25%');
    expect(search.querySelector('.prj-remaining')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('3 / 4');
    expect(search.querySelector('.prj-when')?.getAttribute('title')).toBeTruthy();
    expect(root.querySelector('tr[data-key="W-F10"] .wrk-chip')?.getAttribute('data-status')).toBe('complete');
    expect(root.querySelector('tr[data-key="W-F3"] .prj-flags')).toBeNull();
    expect([...root.querySelectorAll('.cg-segmented button')].map(button => button.textContent?.replace(/\s+/g, ' ').trim())).toEqual(['All 5', 'Open 4', 'Complete 1']);
    expect(root.querySelector('.prj-legend')?.textContent).toContain('in progress / in review');
    expect(root.querySelector('.prj-clear')).toBeNull();
  });

  it('sorts by header clicks: the active column flips, a new one sorts descending, the key ascending', async () => {
    const { root, rows, click, navigate } = await render();
    const header = (sort: string) => root.querySelector(`.prj-sort[data-sort="${sort}"]`);
    expect(header('updated')?.closest('th')?.getAttribute('aria-sort')).toBe('descending');
    expect(header('updated')?.textContent).toContain('↓');
    expect(root.querySelectorAll('.prj-ftable th button')).toHaveLength(5);

    await click(header('updated'));
    expect(rows()).toEqual(['W-F4', 'W-F10', 'W-F3', 'W-F1', 'W-F2']);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { dir: 'asc' }, replaceUrl: true }));

    await click(header('key'));
    expect(rows()).toEqual(['W-F1', 'W-F2', 'W-F3', 'W-F4', 'W-F10']);
    expect(header('key')?.closest('th')?.getAttribute('data-active')).toBe('true');
    expect(header('updated')?.closest('th')?.getAttribute('data-active')).toBe('false');
    expect(header('key')?.textContent).toContain('↑');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { sort: 'key', dir: 'asc' } }));

    await click(header('remaining'));
    expect(rows()).toEqual(['W-F4', 'W-F2', 'W-F1', 'W-F10', 'W-F3']);
    expect(root.querySelector('.prj-dir')?.textContent).toBe('↓ desc');
    await click(root.querySelector('.prj-dir'));
    expect(rows()).toEqual(['W-F10', 'W-F3', 'W-F2', 'W-F1', 'W-F4']);
    expect(root.querySelector('.prj-dir')?.textContent).toBe('↑ asc');
  });

  it('filters by toggles and status, shows the empty state and clears the filters', async () => {
    const { root, rows, click, navigate } = await render();
    const inProg = root.querySelector('.prj-pill[data-dot="blue"]');
    await click(inProg);
    expect(inProg?.getAttribute('aria-pressed')).toBe('true');
    expect(rows()).toEqual(['W-F1', 'W-F4']);
    expect(root.querySelector('.prj-shown')?.textContent).toBe('2 of 5 features');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { inprog: '1' }, replaceUrl: true }));

    await click(root.querySelector('.cg-segmented [data-status="complete"]'));
    expect(rows()).toEqual([]);
    // The status counts ignore the other filters.
    expect(root.querySelector('.cg-segmented [data-status="complete"]')?.textContent).toContain('1');
    const empty = root.querySelector('.prj-features-empty');
    expect(empty?.textContent).toContain('No features match these filters.');
    expect(root.querySelector('.prj-clear')).not.toBeNull();

    await click(empty?.querySelector('button'));
    expect(rows()).toHaveLength(5);
    expect(root.querySelector('.prj-clear')).toBeNull();
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: {} }));
  });

  it('filters live on the search and the creator', async () => {
    const { root, rows, fixture, navigate } = await render();
    const input = root.querySelector('.prj-search input') as HTMLInputElement;
    expect(input.placeholder).toBe('Filter by key or title');
    input.value = 'SEARCH';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(rows()).toEqual(['W-F2', 'W-F4']);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'SEARCH' }, replaceUrl: true }));

    const select = root.querySelector('.prj-select select') as HTMLSelectElement;
    expect([...select.options].map(option => option.textContent)).toEqual(['Anyone', 'alice', 'michael', 'bob']);
    select.value = 'alice';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(rows()).toEqual(['W-F2', 'W-F4']);
    input.value = '';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(rows()).toEqual(['W-F2', 'W-F4']);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { by: 'alice' } }));
  });

  it('restores the view from the query params', async () => {
    const { root, rows } = await render({ q: 'f', status: 'open', uncovered: '1', sort: 'key', dir: 'desc' });
    expect(rows()).toEqual(['W-F2', 'W-F1']);
    expect((root.querySelector('.prj-search input') as HTMLInputElement).value).toBe('f');
    expect(root.querySelector('.cg-segmented [data-status="open"]')?.classList).toContain('active');
    expect(root.querySelector('.prj-pill[data-dot="amber"]')?.getAttribute('aria-pressed')).toBe('true');
    expect((root.querySelectorAll('.prj-select select')[1] as HTMLSelectElement).value).toBe('key');
    expect(root.querySelector('.prj-dir')?.textContent).toBe('↓ desc');
    expect(root.querySelector('.prj-shown')?.textContent).toBe('2 of 5 features');
  });

  it('reads everything again when the revision changes', async () => {
    const { fixture, api } = await render();
    expect(api.features).toHaveBeenCalledTimes(1);
    fixture.componentInstance.revision.set(2);
    await settle(fixture);
    expect(api.features).toHaveBeenCalledTimes(2);
    expect(api.sessions).toHaveBeenCalledTimes(2);
  });

  it('creates a feature from the New feature dialog in the project', async () => {
    const { root, click, api, fixture } = await render();
    const button = [...root.querySelectorAll('.prj-features-head button')].find(b => b.textContent?.trim() === 'New feature');
    await click(button);
    expect(root.querySelector('wrk-dialog')?.textContent).toContain('New feature');
    const work = fixture.debugElement.query(debug => debug.componentInstance instanceof ProjectWorkComponent).componentInstance as ProjectWorkComponent;
    await work.createFeature({ title: 'Reports' });
    await settle(fixture);
    expect(api.create).toHaveBeenCalledWith({ project: 'W', feature: { title: 'Reports' }, idempotency_key: expect.stringMatching(/^ui-feature-/) });
    expect(root.querySelector('wrk-dialog')).toBeNull();
    expect(api.features).toHaveBeenCalledTimes(2);
  });

  it('says when the project has no features yet', async () => {
    const { root } = await render({}, { features: vi.fn().mockResolvedValue([]) });
    expect(root.querySelector('.prj-features-empty')?.textContent).toContain('No features yet');
    expect(root.querySelector('.prj-shown')?.textContent).toBe('0 features');
  });
});
