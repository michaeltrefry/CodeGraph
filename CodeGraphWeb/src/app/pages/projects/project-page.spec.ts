import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { WorkApiService } from '../work/work-api.service';
import { fiftyItemFeature } from '../work/work-fixtures';
import { WorkLiveService } from '../work/work-live.service';
import { FeatureView, ProjectView } from '../work/work.models';
import { ProjectSignalsService, inProgressItems, inProgressRow, reviewRow, reviewState } from './project-data';
import { fakeLive, project, review, settle } from './project-fixtures';
import { ProjectPageComponent } from './project-page.component';
import { ProjectWorkComponent } from './project-work.component';
import { DreamingPageComponent } from '../dreaming/dreaming-page.component';
import { KnowledgePageComponent } from '../knowledge/knowledge-page.component';
import { LessonsPageComponent } from '../knowledge/lessons-page.component';
import { MemoryPageComponent } from '../memory/memory-page.component';
import { ReviewQueuePageComponent } from '../knowledge/review-queue-page.component';

const template = '<i class="stub-project">{{ project() }}</i>';
@Component({ selector: 'app-review-queue-page', standalone: true, template }) class ApprovalsStub { readonly project = input<string | undefined>(); }
@Component({ selector: 'app-knowledge-page', standalone: true, template }) class KnowledgeStub { readonly project = input<string | undefined>(); }
@Component({ selector: 'app-lessons-page', standalone: true, template }) class LessonsStub { readonly project = input<string | undefined>(); }
@Component({ selector: 'app-dreaming-page', standalone: true, template }) class DreamingStub { readonly project = input<string | undefined>(); }
@Component({ selector: 'app-memory-page', standalone: true, template }) class MemoryStub { readonly project = input<string | undefined>(); }
@Component({ selector: 'app-project-work', standalone: true, template: '<i class="stub-project">{{ project().key }}</i><i class="stub-revision">{{ revision() }}</i>' })
class WorkStub { readonly project = input.required<ProjectView>(); readonly revision = input(0); }
const embedded = [ReviewQueuePageComponent, KnowledgePageComponent, LessonsPageComponent, MemoryPageComponent, DreamingPageComponent, ProjectWorkComponent];
const stubs = [ApprovalsStub, KnowledgeStub, LessonsStub, MemoryStub, DreamingStub, WorkStub];

describe('overview mapping', () => {
  it('gives each latest review its chip: failed wins, queued is running, a missing stale flag is not stale', () => {
    expect(reviewState({ status: 'failed', stale: true })).toBe('failed');
    expect(reviewState({ status: 'queued' })).toBe('running');
    expect(reviewState({ status: 'running' })).toBe('running');
    expect(reviewState({ status: 'completed', stale: true })).toBe('stale');
    expect(reviewState({ status: 'completed' })).toBe('completed');

    expect(reviewRow('codegraph', review({ status: 'completed', stale: true }, ['low', 'high', 'medium', 'high', 'low', 'low'])))
      .toMatchObject({ state: 'stale', label: 'Stale', chip: 'cg-chip-warn', summary: '2 high · 1 medium · 3 low findings', at: '2026-01-02T01:00:00Z' });
    expect(reviewRow('codegraph', review({ status: 'running', completedAt: undefined, startedAt: '2026-01-02T00:30:00Z' })))
      .toMatchObject({ state: 'running', label: 'Running', chip: 'cg-chip-accent', summary: 'Review in progress…', at: '2026-01-02T00:30:00Z' });
    expect(reviewRow('codegraph', review({ status: 'failed' }))).toMatchObject({ chip: 'cg-chip-err', summary: 'No findings — run failed' });
    expect(reviewRow('codegraph', review({ status: 'completed' }))).toMatchObject({ chip: 'cg-chip-ok', label: 'Completed', summary: 'No findings' });
  });

  it('keeps in progress, in review and awaiting-a-person items, those waiting on a person first', () => {
    const items = fiftyItemFeature();
    const kept = inProgressItems(items).map(item => item.key);
    expect(kept[0]).toBe('W-33');
    expect([...kept].sort()).toEqual(['W-2', 'W-33', 'W-36', 'W-39', 'W-42', 'W-48'].sort());
    // Blocked for other reasons (waiting, cancelled prerequisite) is not in progress.
    expect(kept).not.toContain('W-31');
    expect(kept).not.toContain('W-10');

    const row = inProgressRow(items.find(item => item.key === 'W-2')!);
    expect(row).toMatchObject({ key: 'W-2', meta: 'W-F1 · claude-code', status: 'in_progress', awaitingHuman: false });
    expect(inProgressRow(items.find(item => item.key === 'W-33')!)).toMatchObject({ meta: 'W-F1 · unclaimed', awaitingHuman: true });
  });
});

describe('ProjectPageComponent', () => {
  const feature = (key: string, status: 'open' | 'complete'): FeatureView => ({
    kind: 'feature', id: key, key, project_id: 'p', project_key: 'W', title: key, documents: [], version: 1, created_by: 'michael',
    created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', status
  });

  async function render(tab: string, over: Record<string, unknown> = {}) {
    const params = new BehaviorSubject(convertToParamMap({ key: 'W', tab }));
    const api = {
      get: vi.fn().mockResolvedValue(project),
      items: vi.fn().mockResolvedValue(fiftyItemFeature()),
      features: vi.fn().mockResolvedValue([feature('W-F1', 'open'), feature('W-F2', 'complete'), feature('W-F3', 'open')]),
      update: vi.fn().mockResolvedValue({ project }),
      ...over
    };
    const signals = {
      openApprovals: vi.fn().mockResolvedValue(4),
      latestReviews: vi.fn().mockResolvedValue([review({ status: 'completed', stale: true }, ['high', 'low']), null])
    };
    await TestBed.configureTestingModule({
      imports: [ProjectPageComponent],
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: WorkApiService, useValue: api },
        { provide: WorkLiveService, useValue: fakeLive },
        { provide: ProjectSignalsService, useValue: signals },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: params.value }, paramMap: params } }
      ]
    }).overrideComponent(ProjectPageComponent, { remove: { imports: embedded }, add: { imports: stubs } }).compileComponents();
    const fixture = TestBed.createComponent(ProjectPageComponent);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, api, signals, params, root: fixture.nativeElement as HTMLElement };
  }

  it('shows the head, the subline and the tabs with Work and Approvals counts', async () => {
    const { root, api } = await render('overview');
    expect(api.get).toHaveBeenCalledWith('W');
    expect(root.querySelector('.wrk-head .wrk-key')?.textContent?.trim()).toBe('W · v2');
    expect(root.querySelector('h1')?.textContent).toBe('Workbench');
    expect(root.querySelector('.wrk-live')?.textContent?.trim()).toBe('live');
    expect([...root.querySelectorAll('.prj-actions button')].map(button => button.textContent?.trim())).toEqual(['Claim next item', 'Import plan', 'Edit project', 'New item']);
    expect(root.querySelector('.prj-subline')?.textContent).toMatch(/Created by michael · 2 repositories · updated .+ago/);
    const tabs = [...root.querySelectorAll('.wrk-tab')];
    expect(tabs.map(tab => tab.querySelector('span')?.textContent)).toEqual(['Overview', 'Work', 'Approvals', 'Knowledge', 'Lessons', 'Memory', 'Dreaming']);
    expect(root.querySelector('[data-tab="overview"]')?.getAttribute('aria-selected')).toBe('true');
    expect(root.querySelector('[data-tab="work"] .prj-tab-count')?.textContent).toBe('3');
    expect(root.querySelector('[data-tab="approvals"] .prj-tab-count')?.textContent).toBe('4');
  });

  it('shows about, in-progress items, latest code reviews, stats and repositories on the overview', async () => {
    const { root, api } = await render('overview');
    expect(api.items).toHaveBeenCalledWith('W', { status: ['backlog', 'ready', 'in_progress', 'in_review'] });
    expect(root.querySelector('.prj-about')?.textContent).toContain('Coordinates agent work.');
    expect([...root.querySelectorAll('.prj-goal')].map(goal => goal.textContent)).toEqual(['Ship the UI', 'Keep it live']);

    const items = [...root.querySelectorAll('.prj-item')];
    expect(items.map(item => item.getAttribute('data-key'))[0]).toBe('W-33');
    expect(items).toHaveLength(6);
    expect(items[0].textContent).toContain('awaiting you');
    expect(root.querySelector('.prj-item[data-key="W-36"] .wrk-chip')?.getAttribute('data-status')).toBe('in_review');
    expect(root.querySelector('.prj-item[data-key="W-2"]')?.textContent).toContain('W-F1 · claude-code');

    const reviewItem = root.querySelector('[data-repo="codegraph"]')!;
    expect(reviewItem.querySelector('.cg-chip')?.classList).toContain('cg-chip-warn');
    expect(reviewItem.querySelector('.cg-chip')?.textContent).toBe('Stale');
    expect(reviewItem.textContent).toContain('1 high · 1 low findings');
    expect(root.textContent).toContain('Not reviewed yet: shared-lib');
    expect(root.querySelector('a[href="/projects/W/code-reviews"]')?.textContent).toContain('All code reviews');
    expect(root.querySelector('.prj-h-link[href="/projects/W/work"]')?.textContent).toContain('Open Work');

    const stats = [...root.querySelectorAll('.prj-stat')].map(stat => [...stat.children].map(part => part.textContent?.trim()).join(' '));
    expect(stats).toEqual(['40% finished', '3 in flight', '2 open features']);
    expect([...root.querySelectorAll('.prj-repo')].map(repo => [...repo.children].map(part => part.textContent?.trim()).join(' '))).toEqual(['codegraph owned', 'shared-lib dependency']);
  });

  it('opens the project edit dialog from Edit description and saves with the version read', async () => {
    const { fixture, root, api } = await render('overview');
    const edit = [...root.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Edit description') as HTMLButtonElement;
    edit.click();
    fixture.detectChanges();
    expect(root.querySelector('wrk-dialog')?.textContent).toContain('Edit project');
    await fixture.componentInstance.saveProject({ description: 'New words' });
    expect(api.update).toHaveBeenCalledWith('W', { expected_version: 2, description: 'New words' });
  });

  it('embeds the Work tab given the route project and the revision, and redirects an unknown tab to the overview', async () => {
    const { fixture, root, params } = await render('work');
    expect(root.querySelector('[data-tab="work"]')?.getAttribute('aria-selected')).toBe('true');
    expect(root.querySelector('app-project-overview')).toBeNull();
    expect(root.querySelector('[role="tabpanel"] app-project-work .stub-project')?.textContent).toBe('W');
    expect(Number(root.querySelector('[role="tabpanel"] app-project-work .stub-revision')?.textContent)).toBeGreaterThan(0);

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');
    params.next(convertToParamMap({ key: 'W', tab: 'nope' }));
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith(['/projects', 'W', 'overview'], { replaceUrl: true });
  });

  it.each([
    ['approvals', 'app-review-queue-page'],
    ['knowledge', 'app-knowledge-page'],
    ['lessons', 'app-lessons-page'],
    ['memory', 'app-memory-page'],
    ['dreaming', 'app-dreaming-page']
  ])('embeds the %s page scoped to the route project', async (tab, selector) => {
    const { root } = await render(tab);
    expect(root.querySelector(`[role="tabpanel"] ${selector} .stub-project`)?.textContent).toBe('W');
    expect(root.querySelector('app-project-overview')).toBeNull();
  });

  it('shows the new project when only the route key changes', async () => {
    const other: ProjectView = { ...project, key: 'X', name: 'Other', version: 7 };
    const get = vi.fn((key: string) => Promise.resolve(key === 'X' ? other : project));
    const { fixture, root, params } = await render('overview', { get });
    expect(root.querySelector('h1')?.textContent).toBe('Workbench');
    params.next(convertToParamMap({ key: 'X', tab: 'overview' }));
    await settle(fixture);
    expect(get).toHaveBeenLastCalledWith('X');
    expect(root.querySelector('h1')?.textContent).toBe('Other');
    expect(root.querySelector('.wrk-head .wrk-key')?.textContent?.trim()).toBe('X · v7');
  });
});
