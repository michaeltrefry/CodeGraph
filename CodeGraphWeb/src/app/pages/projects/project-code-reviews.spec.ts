import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { RepositoryReviewRunSummary } from '../../core/models';
import { WorkApiService } from '../work/work-api.service';
import { ProjectCodeReviewsComponent } from './project-code-reviews.component';
import { project, settle } from './project-fixtures';
import { RepoRuns, mergeRuns, severityChips } from './project-reviews-data';

function summary(over: Partial<RepositoryReviewRunSummary>): RepositoryReviewRunSummary {
  return {
    id: 1, repository: 'codegraph', status: 'completed', reviewMode: 'full', reviewedCommitSha: 'abcdef1234567890', model: 'opus',
    requestedBy: 'michael', createdAt: '2026-01-02T00:00:00Z', completedAt: '2026-01-02T01:00:00Z', stale: false, findingCounts: {}, ...over
  };
}

const codegraph: RepoRuns = {
  nextBefore: null,
  items: [
    summary({ id: 9, createdAt: '2026-01-05T00:00:00Z', findingCounts: { high: 2, medium: 5, low: 7 } }),
    summary({ id: 4, createdAt: '2026-01-01T00:00:00Z', stale: true, findingCounts: { low: 1 } })
  ]
};
const shared: RepoRuns = {
  nextBefore: null,
  items: [
    summary({ id: 7, repository: 'shared-lib', status: 'failed', createdAt: '2026-01-04T00:00:00Z', completedAt: undefined }),
    summary({ id: 3, repository: 'shared-lib', createdAt: '2026-01-02T00:00:00Z' })
  ]
};

describe('code review run mapping', () => {
  it('merges the repositories newest first and marks the newest run of each repository as latest', () => {
    const rows = mergeRuns({ codegraph, 'shared-lib': shared }, ['codegraph', 'shared-lib']);
    expect(rows.map(row => [row.repository, row.runId, row.latest])).toEqual([
      ['codegraph', 9, true], ['shared-lib', 7, true], ['shared-lib', 3, false], ['codegraph', 4, false]
    ]);
  });

  it('keeps the latest pill on the repository newest run when filtered to one repository', () => {
    expect(mergeRuns({ codegraph, 'shared-lib': shared }, ['shared-lib']).map(row => [row.runId, row.latest])).toEqual([[7, true], [3, false]]);
  });

  it('maps status, findings, commit and the action', () => {
    const row = (run: RepositoryReviewRunSummary) => mergeRuns({ codegraph: { items: [run], nextBefore: null } }, ['codegraph'])[0];
    const running = row(summary({ status: 'queued', findingCounts: {} }));
    const failed = row(summary({ id: 2, status: 'failed', stale: true }));
    const completed = row(summary({ id: 3 }));
    const stale = row(summary({ id: 4, stale: true, findingCounts: { critical: 1, high: 2, medium: 5, low: 7, info: 1 }, reviewedCommitSha: undefined, model: undefined, requestedBy: undefined }));
    expect(running).toMatchObject({ state: 'running', label: 'Running', chip: 'cg-chip-accent', noFindings: 'In progress…', action: 'Watch' });
    expect(failed).toMatchObject({ state: 'failed', chip: 'cg-chip-err', noFindings: 'No findings — run failed', action: 'Retry' });
    expect(completed).toMatchObject({ state: 'completed', chip: 'cg-chip-ok', noFindings: 'No findings', action: 'Open', commit: 'abcdef1', model: 'opus', startedBy: 'michael' });
    expect(stale).toMatchObject({ state: 'stale', label: 'Stale', chip: 'cg-chip-warn', action: 'Open', commit: '—', model: '—', startedBy: '—', noFindings: '' });
    expect(stale.severities.map(chip => [chip.label, chip.chip])).toEqual([
      ['1 critical', 'cg-chip-err'], ['2 high', 'cg-chip-err'], ['5 medium', 'cg-chip-warn'], ['7 low', ''], ['1 info', '']
    ]);
  });

  it('leaves zero counts out of the severity chips', () => {
    expect(severityChips({ high: 0, low: 3 }).map(chip => chip.label)).toEqual(['3 low']);
    expect(severityChips(undefined)).toEqual([]);
  });
});

describe('ProjectCodeReviewsComponent', () => {
  const pages: Record<string, { items: RepositoryReviewRunSummary[]; nextBefore: number | null }[]> = {
    codegraph: [{ items: codegraph.items, nextBefore: null }],
    'shared-lib': [{ items: shared.items, nextBefore: null }]
  };

  async function render(query: Record<string, string> = {}, over: { list?: ReturnType<typeof vi.fn>; start?: ReturnType<typeof vi.fn>; project?: unknown } = {}) {
    const queryParams = new BehaviorSubject(convertToParamMap(query));
    const list = over.list ?? vi.fn((repo: string) => of(pages[repo][0]));
    const start = over.start ?? vi.fn().mockReturnValue(of({ reviewRunId: 10, status: 'queued' }));
    await TestBed.configureTestingModule({
      imports: [ProjectCodeReviewsComponent],
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: WorkApiService, useValue: { get: vi.fn().mockResolvedValue(over.project ?? project) } },
        { provide: ApiService, useValue: { listRepositoryReviews: list, startRepositoryReview: start } },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ key: 'W' })), queryParamMap: queryParams } }
      ]
    }).compileComponents();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(ProjectCodeReviewsComponent);
    fixture.detectChanges();
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const text = (el: Element | null | undefined) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
    return { fixture, root, list, start, navigate, queryParams, text };
  }

  it('lists the runs of every member repository newest first with chips, fallbacks and the latest pill', async () => {
    const { root, text, list } = await render();
    expect(list).toHaveBeenCalledWith('codegraph', 20, undefined);
    expect(list).toHaveBeenCalledWith('shared-lib', 20, undefined);
    expect(text(root.querySelector('a.cg-mono'))).toBe('← W · Workbench');
    expect(text(root.querySelector('h1'))).toBe('Code reviews');
    expect(text(root.querySelector('.cg-field-label'))).toBe('Repository');
    expect([...root.querySelectorAll('select option')].map(o => text(o))).toEqual(['All repositories', 'codegraph', 'shared-lib']);

    const rows = [...root.querySelectorAll<HTMLElement>('tbody tr')];
    expect(rows.map(row => row.dataset['run'])).toEqual(['codegraph#9', 'shared-lib#7', 'shared-lib#3', 'codegraph#4']);
    expect(rows.map(row => !!row.querySelector('.prj-latest'))).toEqual([true, true, false, false]);
    expect(root.querySelector('.cg-card > .prj-scroll > table.cg-table')).not.toBeNull();

    const first = rows[0];
    expect([...first.querySelectorAll('.prj-sev .cg-chip')].map(chip => [text(chip), chip.className.includes('cg-chip-err'), chip.className.includes('cg-chip-warn')])).toEqual([
      ['2 high', true, false], ['5 medium', false, true], ['7 low', false, false]
    ]);
    expect(text(first.querySelector('[data-state]'))).toBe('Completed');
    expect(text(first.querySelector('.prj-commit'))).toBe('abcdef1');
    expect(text(first.querySelector('.prj-by'))).toBe('michael');
    expect(first.querySelector<HTMLElement>('.prj-when')?.title).toBeTruthy();
    expect(text(first.querySelector('button.wrk-btn'))).toBe('Open');

    expect(rows[1].querySelector('[data-state]')?.className).toContain('cg-chip-err');
    expect(text(rows[1].querySelector('.prj-no-findings'))).toBe('No findings — run failed');
    expect(text(rows[1].querySelector('button.wrk-btn'))).toBe('Retry');
    expect(text(rows[2].querySelector('.prj-no-findings'))).toBe('No findings');
    expect(text(rows[3].querySelector('[data-state]'))).toBe('Stale');
    expect(rows[3].querySelector('[data-state]')?.className).toContain('cg-chip-warn');
  });

  it('shows a queued or running run as Running with In progress… and a Watch action', async () => {
    const running = { items: [summary({ id: 12, status: 'running', completedAt: undefined, startedAt: '2026-01-06T00:00:00Z', createdAt: '2026-01-06T00:00:00Z' })], nextBefore: null };
    const { root, text, navigate } = await render({}, { list: vi.fn((repo: string) => of(repo === 'codegraph' ? running : { items: [], nextBefore: null })) });
    const row = root.querySelector<HTMLElement>('tbody tr')!;
    expect(text(row.querySelector('[data-state]'))).toBe('Running');
    expect(row.querySelector('[data-state]')?.className).toContain('cg-chip-accent');
    expect(text(row.querySelector('.prj-no-findings'))).toBe('In progress…');
    expect(text(row.querySelector('button.wrk-btn'))).toBe('Watch');
    row.querySelector<HTMLButtonElement>('button.wrk-btn')!.click();
    expect(navigate).toHaveBeenCalledWith(['/repos', 'codegraph'], { queryParams: { tab: 'reviews' } });
  });

  it('filters to the repository of ?repo= and changes it through the query parameter', async () => {
    const { root, navigate, fixture } = await render({ repo: 'shared-lib' });
    expect([...root.querySelectorAll<HTMLElement>('tbody tr')].map(row => row.dataset['run'])).toEqual(['shared-lib#7', 'shared-lib#3']);
    expect(root.querySelector<HTMLSelectElement>('select')!.value).toBe('shared-lib');

    const select = root.querySelector<HTMLSelectElement>('select')!;
    select.value = 'codegraph';
    select.dispatchEvent(new Event('change'));
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { repo: 'codegraph' }, queryParamsHandling: 'merge' }));
    select.value = '';
    select.dispatchEvent(new Event('change'));
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { repo: null } }));
    fixture.detectChanges();
  });

  it('ignores a ?repo= that is not a member', async () => {
    const { root } = await render({ repo: 'elsewhere' });
    expect(root.querySelectorAll('tbody tr').length).toBe(4);
    expect(root.querySelector<HTMLSelectElement>('select')!.value).toBe('');
  });

  it('disables Run code review for all repositories and starts a review of the selected repository, then lists the new run', async () => {
    const list = vi.fn()
      .mockImplementation((repo: string) => of(pages[repo][0]));
    const { root, start, fixture, text } = await render({}, { list });
    const run = [...root.querySelectorAll<HTMLButtonElement>('button.cg-btn.primary')].find(button => text(button) === 'Run code review')!;
    expect(run.disabled).toBe(true);
    expect(run.title).toBe('Select a repository to run a code review');
    fixture.destroy();

    TestBed.resetTestingModule();
    list.mockClear();
    const second = await render({ repo: 'codegraph' }, { list });
    const button = [...second.root.querySelectorAll<HTMLButtonElement>('button.cg-btn.primary')][0];
    expect(button.disabled).toBe(false);
    // The next read of the history includes the run just queued.
    const queued = summary({ id: 10, status: 'queued', createdAt: '2026-01-06T00:00:00Z', completedAt: undefined });
    list.mockImplementation((repo: string) => of({ items: repo === 'codegraph' ? [queued, ...codegraph.items] : shared.items, nextBefore: null }));
    button.click();
    await settle(second.fixture);
    expect(second.start).toHaveBeenCalledWith('codegraph');
    expect(start).not.toHaveBeenCalled();
    expect(second.text(second.root.querySelector('[role=status]'))).toBe('Started a code review of codegraph.');
    expect([...second.root.querySelectorAll<HTMLElement>('tbody tr')].map(row => row.dataset['run'])).toEqual(['codegraph#10', 'codegraph#9', 'codegraph#4']);
  });

  it('runs the only repository of a single-repository project without a selection', async () => {
    const { root, start, text, fixture } = await render({}, { project: { ...project, repositories: [project.repositories[0]] } });
    const button = [...root.querySelectorAll<HTMLButtonElement>('button.cg-btn.primary')].find(b => text(b) === 'Run code review')!;
    expect(button.disabled).toBe(false);
    button.click();
    await settle(fixture);
    expect(start).toHaveBeenCalledWith('codegraph');
  });

  it('retries a failed run by starting a new review of its repository, and opens a completed one on the repository reviews tab', async () => {
    const { root, start, navigate, fixture } = await render();
    const rows = [...root.querySelectorAll<HTMLElement>('tbody tr')];
    rows[0].querySelector<HTMLButtonElement>('button.wrk-btn')!.click();
    expect(navigate).toHaveBeenCalledWith(['/repos', 'codegraph'], { queryParams: { tab: 'reviews' } });
    expect(start).not.toHaveBeenCalled();

    rows[1].querySelector<HTMLButtonElement>('button.wrk-btn')!.click();
    await settle(fixture);
    expect(start).toHaveBeenCalledWith('shared-lib');
  });

  it('shows why a review could not be started', async () => {
    const { root, fixture } = await render({ repo: 'codegraph' }, { start: vi.fn().mockReturnValue(throwError(() => new Error('A review is already running'))) });
    root.querySelector<HTMLButtonElement>('button.cg-btn.primary')!.click();
    await settle(fixture);
    expect(root.querySelector('[role=alert]')?.textContent).toContain('Could not start a code review of codegraph: A review is already running');
  });

  it('loads older pages with Load more and drops the button on the last page', async () => {
    const older = summary({ id: 2, createdAt: '2025-12-01T00:00:00Z' });
    const list = vi.fn((repo: string, _limit: number, before?: number) =>
      of(repo === 'codegraph'
        ? (before === undefined ? { items: codegraph.items, nextBefore: 4 } : { items: [older], nextBefore: null })
        : { items: shared.items, nextBefore: null }));
    const { root, fixture, text } = await render({}, { list });
    const more = () => [...root.querySelectorAll<HTMLButtonElement>('.prj-more button')][0];
    expect(text(more())).toBe('Load more');
    more().click();
    await settle(fixture);
    expect(list).toHaveBeenCalledWith('codegraph', 20, 4);
    expect(list).not.toHaveBeenCalledWith('shared-lib', 20, expect.anything());
    expect([...root.querySelectorAll<HTMLElement>('tbody tr')].map(row => row.dataset['run'])).toEqual(['codegraph#9', 'shared-lib#7', 'shared-lib#3', 'codegraph#4', 'codegraph#2']);
    expect(more()).toBeUndefined();
  });

  it('says so when a repository history cannot be read, and when the project has no repositories', async () => {
    const failing = await render({}, { list: vi.fn((repo: string) => repo === 'codegraph' ? throwError(() => new Error('boom')) : of(pages[repo][0])) });
    expect(failing.root.textContent).toContain('Could not read the review history of codegraph.');
    expect(failing.root.querySelectorAll('tbody tr').length).toBe(2);
    failing.fixture.destroy();

    TestBed.resetTestingModule();
    const empty = await render({}, { project: { ...project, repositories: [] } });
    expect(empty.root.textContent).toContain('This project has no repositories.');
  });
});
