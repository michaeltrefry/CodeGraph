import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom, map } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { WorkApiService } from '../work/work-api.service';
import { AgoPipe } from '../work/work-shared';
import { ProjectView } from '../work/work.models';
import { RepoRuns, ReviewRunRow, foldFirstPage, mergeRuns } from './project-reviews-data';

/** Runs read per repository per request; "Load more" reads the next older page of each repository. */
export const REVIEW_PAGE_SIZE = 20;

/**
 * Code reviews of a project's repositories: /projects/:key/code-reviews. Every member repository's run
 * history merged newest first, filterable to one repository (?repo=). Open and Watch go to the repository
 * page's reviews tab, which shows the review and follows a running one live.
 */
@Component({
  selector: 'app-project-code-reviews',
  standalone: true,
  imports: [RouterLink, AgoPipe, DatePipe],
  styleUrl: './projects.scss',
  template: `
    <div class="cg-page">
      <header class="cg-page-header">
        <div class="prj-title">
          <a class="cg-mono cg-small" [routerLink]="['/projects', key(), 'overview']">← {{ key() }}@if (project(); as p) { · {{ p.name }} }</a>
          <h1>Code reviews</h1>
          <p>AI code review runs across this project's repositories, newest first.</p>
        </div>
        <div class="prj-actions prj-review-actions">
          <label class="cg-field">
            <span class="cg-field-label">Repository</span>
            <select class="cg-select" [value]="selected()" (change)="select($any($event.target).value)" [disabled]="repositories().length === 0">
              <option value="" [selected]="selected() === ''">All repositories</option>
              @for (repository of repositories(); track repository) {
                <option [value]="repository" [selected]="selected() === repository">{{ repository }}</option>
              }
            </select>
          </label>
          <button type="button" class="cg-btn primary" (click)="run(runTarget())" [disabled]="!runTarget() || busy() !== ''"
            [title]="runTarget() ? 'Start a code review of ' + runTarget() : 'Select a repository to run a code review'">Run code review</button>
        </div>
      </header>

      @if (notice()) { <div class="wrk-ok" role="status">{{ notice() }}</div> }
      @if (error()) { <div class="wrk-error" role="alert">{{ error() }}</div> }
      @if (failedRepositories().length > 0) {
        <div class="wrk-error" role="alert">Could not read the review history of {{ failedRepositories().join(', ') }}.</div>
      }

      <div class="cg-card">
        <div class="prj-scroll">
          <table class="cg-table prj-reviews">
            <thead>
              <tr>
                <th>Repository</th><th>Status</th><th>Findings</th><th>Commit</th><th>Model</th><th>Started by</th>
                <th class="prj-num">Reviewed</th><th><span class="prj-sr">Action</span></th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.key) {
                <tr [attr.data-run]="row.key">
                  <td class="prj-repo-cell"><span class="cg-mono cg-small">{{ row.repository }}</span>@if (row.latest) { <span class="prj-latest">latest</span> }</td>
                  <td><span class="cg-chip cg-chip-dot {{ row.chip }}" [attr.data-state]="row.state">{{ row.label }}</span></td>
                  <td>
                    <div class="prj-sev">
                      @for (severity of row.severities; track severity.severity) {
                        <span class="cg-chip {{ severity.chip }}" [attr.data-severity]="severity.severity">{{ severity.label }}</span>
                      }
                      @if (row.noFindings) { <span class="cg-muted cg-small prj-no-findings">{{ row.noFindings }}</span> }
                    </div>
                  </td>
                  <td class="cg-mono cg-small prj-commit">{{ row.commit }}</td>
                  <td class="cg-small prj-model">{{ row.model }}</td>
                  <td class="cg-mono cg-small prj-by">{{ row.startedBy }}</td>
                  <td class="prj-num prj-when cg-muted cg-small" [title]="row.at | date: 'medium'">{{ row.at | ago }}</td>
                  <td class="prj-num">
                    <button type="button" class="wrk-btn" [disabled]="busy() !== ''" (click)="act(row)">{{ row.action }}</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (loading()) { <div class="wrk-empty prj-empty">Loading…</div> }
        @else if (project() && repositories().length === 0) { <div class="wrk-empty prj-empty">This project has no repositories.</div> }
        @else if (rows().length === 0 && !error()) { <div class="wrk-empty prj-empty">No code reviews yet.</div> }
      </div>

      @if (hasMore()) {
        <div class="prj-more">
          <button type="button" class="cg-btn" [disabled]="loadingMore()" (click)="loadMore()">{{ loadingMore() ? 'Loading…' : 'Load more' }}</button>
        </div>
      }
    </div>
  `
})
export class ProjectCodeReviewsComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly work = inject(WorkApiService);
  private readonly api = inject(ApiService);

  readonly key = toSignal(this.route.paramMap.pipe(map(params => params.get('key') ?? '')), { initialValue: '' });
  private readonly repoParam = toSignal(this.route.queryParamMap.pipe(map(params => params.get('repo') ?? '')), { initialValue: '' });

  readonly project = signal<ProjectView | null>(null);
  readonly runs = signal<Record<string, RepoRuns>>({});
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  /** The repository a review is being started for. */
  readonly busy = signal('');
  readonly error = signal('');
  readonly notice = signal('');

  readonly repositories = computed(() => (this.project()?.repositories ?? []).map(member => member.repository));
  /** The repository filter: the ?repo= value when it is a member, else all (''). */
  readonly selected = computed(() => (this.repositories().includes(this.repoParam()) ? this.repoParam() : ''));
  private readonly shown = computed(() => (this.selected() ? [this.selected()] : this.repositories()));
  readonly rows = computed<ReviewRunRow[]>(() => mergeRuns(this.runs(), this.shown()));
  /** Run code review needs one repository: the selected one, or the only one the project has. */
  readonly runTarget = computed(() => this.selected() || (this.repositories().length === 1 ? this.repositories()[0] : ''));
  readonly hasMore = computed(() => this.shown().some(repository => this.runs()[repository]?.nextBefore != null));
  readonly failedRepositories = computed(() => this.shown().filter(repository => this.runs()[repository]?.failed));

  /** Bumped when the project changes, so a slow read for a previous project is dropped. */
  private generation = 0;

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => void this.open(params.get('key') ?? ''));
  }

  private async open(key: string): Promise<void> {
    const generation = ++this.generation;
    this.project.set(null);
    this.runs.set({});
    this.error.set('');
    this.notice.set('');
    this.busy.set('');
    this.loading.set(true);
    if (!key) {
      this.loading.set(false);
      return;
    }
    try {
      const project = await this.work.get<ProjectView>(key);
      if (generation !== this.generation) return;
      this.project.set(project);
      const pages = await Promise.all(project.repositories.map(member => this.readPage(member.repository)));
      if (generation !== this.generation) return;
      this.runs.set(Object.fromEntries(project.repositories.map((member, index) => [member.repository, pages[index]])));
    } catch (failure) {
      if (generation === this.generation) this.error.set(failure instanceof Error ? failure.message : 'Could not load the project.');
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }

  private async readPage(repository: string, before?: number): Promise<RepoRuns> {
    try {
      const page = await firstValueFrom(this.api.listRepositoryReviews(repository, REVIEW_PAGE_SIZE, before));
      return { items: page.items, nextBefore: page.nextBefore ?? null };
    } catch {
      return { items: [], nextBefore: null, failed: true };
    }
  }

  select(repository: string): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { repo: repository || null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  /** Reads the next older page of every shown repository that has one. */
  async loadMore(): Promise<void> {
    const generation = this.generation;
    const targets = this.shown().filter(repository => this.runs()[repository]?.nextBefore != null);
    this.loadingMore.set(true);
    try {
      const pages = await Promise.all(targets.map(repository => this.readPage(repository, this.runs()[repository].nextBefore!)));
      if (generation !== this.generation) return;
      this.runs.update(current => {
        const next = { ...current };
        targets.forEach((repository, index) => {
          const page = pages[index];
          const known = current[repository].items;
          next[repository] = page.failed
            ? { ...current[repository], failed: true }
            : { items: [...known, ...page.items.filter(run => !known.some(other => other.id === run.id))], nextBefore: page.nextBefore };
        });
        return next;
      });
    } finally {
      this.loadingMore.set(false);
    }
  }

  /** Starts a review of the repository, then folds its newest runs into the list. */
  async run(repository: string): Promise<void> {
    if (!repository || this.busy()) return;
    this.busy.set(repository);
    this.error.set('');
    this.notice.set('');
    const generation = this.generation;
    try {
      await firstValueFrom(this.api.startRepositoryReview(repository));
      const page = await this.readPage(repository);
      if (generation !== this.generation) return;
      if (!page.failed) this.runs.update(current => ({ ...current, [repository]: foldFirstPage(current[repository], page) }));
      this.notice.set(`Started a code review of ${repository}.`);
    } catch (failure) {
      if (generation === this.generation) this.error.set(`Could not start a code review of ${repository}${failure instanceof Error && failure.message ? `: ${failure.message}` : '.'}`);
    } finally {
      if (generation === this.generation) this.busy.set('');
    }
  }

  act(row: ReviewRunRow): void {
    if (row.action === 'Retry') void this.run(row.repository);
    else void this.router.navigate(['/repos', row.repository], { queryParams: { tab: 'reviews', run: row.runId } });
  }
}
