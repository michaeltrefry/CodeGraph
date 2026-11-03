import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Route, Router, Routes, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { routes } from '../../app.routes';
import { ApiService } from '../../core/api.service';
import { KnowledgeApiService } from '../knowledge/knowledge-api.service';
import { WorkApiService } from '../work/work-api.service';
import { WorkLiveService } from '../work/work-live.service';
import { WorkApiError } from '../work/work.models';
import { projectCrumbs } from './project-crumbs';
import { ProjectSignalsService, projectRow, toReviewCount } from './project-data';
import { fakeLive, project, review, settle } from './project-fixtures';
import { ProjectsListComponent } from './projects-list.component';

describe('projects list mapping', () => {
  it('maps a project to a list row: repositories, progress, in flight and updated', () => {
    const row = projectRow(project, 4);
    expect(row).toMatchObject({ key: 'W', name: 'Workbench', repositories: '2 repositories', pct: 40, inFlight: 3, toReview: 4, updatedAt: project.updated_at });
    expect(row.done).toBeCloseTo(40);
    expect(row.flight).toBeCloseTo(30);
    expect(projectRow({ ...project, repositories: [project.repositories[0]], item_counts: undefined }, undefined))
      .toMatchObject({ repositories: '1 repository', pct: 0, done: 0, flight: 0, inFlight: 0, toReview: undefined });
  });

  it('counts open approvals plus member repositories whose latest review failed or is stale', () => {
    const latest = [
      review({ status: 'failed' }),
      review({ status: 'completed', stale: true }),
      review({ status: 'completed' }),
      review({ status: 'completed', stale: false }),
      review({ status: 'running', stale: true }),
      null
    ];
    expect(toReviewCount(3, latest)).toBe(5);
    expect(toReviewCount(0, [])).toBe(0);
  });

  it('reads breadcrumbs as Projects / name, plus Code reviews on that page', () => {
    expect(projectCrumbs(['projects'], [project]).map(crumb => crumb.label)).toEqual(['Projects']);
    expect(projectCrumbs(['projects', 'W', 'work'], [project])).toEqual([
      { label: 'Projects', url: '/projects' },
      { label: 'Workbench', url: '/projects/W/overview' }
    ]);
    expect(projectCrumbs(['projects', 'W', 'code-reviews'], [project]).map(crumb => crumb.label)).toEqual(['Projects', 'Workbench', 'Code reviews']);
    expect(projectCrumbs(['projects', 'X', 'overview'], [project]).map(crumb => crumb.label)).toEqual(['Projects', 'X']);
  });
});

describe('ProjectSignalsService', () => {
  it('counts every page of the review queue and reads a missing review as none', async () => {
    const reviewQueue = vi.fn()
      .mockReturnValueOnce(of({ items: new Array(50).fill({}), next_cursor: 'c1' }))
      .mockReturnValueOnce(of({ items: new Array(3).fill({}) }));
    const getLatestRepositoryReview = vi.fn((repo: string) => repo === 'codegraph'
      ? of(review({ status: 'failed' }))
      : throwError(() => new HttpErrorResponse({ status: 404 })));
    TestBed.configureTestingModule({
      providers: [
        { provide: KnowledgeApiService, useValue: { reviewQueue } },
        { provide: ApiService, useValue: { getLatestRepositoryReview } }
      ]
    });
    const service = TestBed.inject(ProjectSignalsService);

    expect(await service.openApprovals('W')).toBe(53);
    expect(reviewQueue).toHaveBeenLastCalledWith('W', 50, 'c1');

    const cache = new Map();
    const [first, missing] = await service.latestReviews(['codegraph', 'other'], cache);
    expect(first?.run.status).toBe('failed');
    expect(missing).toBeNull();
    await service.latestReviews(['codegraph'], cache);
    expect(getLatestRepositoryReview).toHaveBeenCalledTimes(2);
  });
});

@Component({ standalone: true, template: '' })
class StubComponent {}

/** The app's route table with every page stubbed and the auth guards dropped: only the paths and redirects are under test. */
function stubbed(table: Routes): Routes {
  return table.map(route => {
    const copy: Route = { ...route, canActivate: undefined, canActivateChild: undefined };
    if (route.loadComponent) copy.loadComponent = () => StubComponent;
    if (route.children) copy.children = stubbed(route.children);
    return copy;
  });
}

describe('project routes', () => {
  async function navigate(url: string): Promise<{ url: string; path: string | undefined }> {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideRouter(stubbed(routes))] });
    const router = TestBed.inject(Router);
    await router.navigateByUrl(url);
    let snapshot = router.routerState.snapshot.root;
    while (snapshot.firstChild) snapshot = snapshot.firstChild;
    return { url: router.url, path: snapshot.routeConfig?.path };
  }

  it('redirects the old review queue and project routes', async () => {
    expect((await navigate('/review-queue')).url).toBe('/approvals');
    expect((await navigate('/work/projects/W')).url).toBe('/projects/W/work');
    expect((await navigate('/work/projects/W/dreaming')).url).toBe('/projects/W/dreaming');
    expect((await navigate('/work/projects/new')).url).toBe('/projects?new=1');
  });

  it('opens a project on its overview and routes code reviews before the tab parameter', async () => {
    expect(await navigate('/projects/W')).toEqual({ url: '/projects/W/overview', path: 'projects/:key/:tab' });
    expect(await navigate('/projects/W/code-reviews')).toEqual({ url: '/projects/W/code-reviews', path: 'projects/:key/code-reviews' });
    expect((await navigate('/projects')).path).toBe('projects');
    expect((await navigate('/approvals')).path).toBe('approvals');
  });
});

describe('ProjectsListComponent', () => {
  async function render(api: object, signals: object, query: Record<string, string> = {}) {
    await TestBed.configureTestingModule({
      imports: [ProjectsListComponent],
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: WorkApiService, useValue: api },
        { provide: WorkLiveService, useValue: fakeLive },
        { provide: ProjectSignalsService, useValue: signals },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectsListComponent);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('shows each project with repositories, progress, in progress, to review and updated time', async () => {
    const signals = {
      openApprovals: vi.fn().mockResolvedValue(2),
      latestReviews: vi.fn().mockResolvedValue([review({ status: 'completed', stale: true }), review({ status: 'completed' })])
    };
    const { root } = await render({ projects: vi.fn().mockResolvedValue([project]) }, signals);
    const cells = [...root.querySelectorAll('tbody tr td')].map(cell => cell.textContent?.trim() ?? '');

    expect(cells[0]).toContain('W');
    expect(cells[0]).toContain('Workbench');
    expect(cells[0]).toContain('Coordinates agent work.');
    expect(cells[1]).toBe('2 repositories');
    expect(cells[2]).toBe('40%');
    expect(root.querySelector('.wrk-progress')?.getAttribute('aria-label')).toBe('40% finished');
    expect(cells[3]).toBe('3');
    expect(cells[4]).toBe('3');
    expect(cells[5]).toMatch(/ago$/);
    expect(signals.latestReviews).toHaveBeenCalledWith(['codegraph', 'shared-lib'], expect.any(Map));
  });

  it('opens a project on its overview when its row is clicked', async () => {
    const { fixture, root } = await render({ projects: vi.fn().mockResolvedValue([project]) }, { openApprovals: vi.fn().mockResolvedValue(0), latestReviews: vi.fn().mockResolvedValue([]) });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    (root.querySelector('tbody tr') as HTMLElement).click();
    await fixture.whenStable();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'W', 'overview']);
  });

  it('creates a project from the New project dialog, repeating its key on a retry, and opens it', async () => {
    const create = vi.fn().mockRejectedValueOnce(new WorkApiError(0, 'network', 'offline')).mockResolvedValue({ created: true, project });
    const api = { create, projects: vi.fn().mockResolvedValue([project]) };
    const { fixture, root } = await render(api, { openApprovals: vi.fn().mockResolvedValue(0), latestReviews: vi.fn().mockResolvedValue([]) }, { new: '1' });
    expect(root.querySelector('wrk-dialog')?.textContent).toContain('New project');

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');
    await fixture.componentInstance.createProject({ key: 'W', name: 'Workbench' });
    fixture.detectChanges();
    expect(root.textContent).toContain('offline');
    await fixture.componentInstance.createProject({ key: 'W', name: 'Workbench' });
    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[0][0].idempotency_key).toBeTruthy();
    expect(create.mock.calls[1][0].idempotency_key).toBe(create.mock.calls[0][0].idempotency_key);
    expect(navigate).toHaveBeenCalledWith(['/projects', 'W', 'overview']);
    expect(fixture.componentInstance.creating()).toBe(false);
  });
});
