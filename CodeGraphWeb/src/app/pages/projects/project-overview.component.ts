import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { RepositoryReviewResponse } from '../../core/models';
import { WorkApiService } from '../work/work-api.service';
import { AgoPipe, statusLabel } from '../work/work-shared';
import { FeatureView, ItemView, ProjectView, WorkStatus } from '../work/work.models';
import { ProjectSignalsService, ReviewRow, inProgressItems, inProgressRow, itemTotals, reviewRow, share } from './project-data';

/**
 * The Overview tab: what the project is for, what is in flight, the latest code review of each member
 * repository, and progress and repositories at a glance. The project comes from the page; this tab
 * reads its own items, features and reviews, again whenever revision changes (the change feed).
 */
@Component({
  selector: 'app-project-overview',
  standalone: true,
  imports: [RouterLink, AgoPipe],
  styleUrl: './projects.scss',
  template: `
    @let p = project();
    <div class="wrk-grid-2">
      <div class="prj-col">
        <section class="wrk-card" aria-labelledby="prj-h-about">
          <h2 id="prj-h-about">About this project<span class="wrk-grow"></span>
            <button type="button" class="wrk-btn wrk-btn-quiet" (click)="editDescription.emit()">Edit description</button></h2>
          @if (p.description) { <p class="prj-about">{{ p.description }}</p> } @else { <div class="wrk-empty">No description yet.</div> }
          @if (p.goals.length > 0) {
            <ul class="prj-goals" aria-label="Goals">@for (goal of p.goals; track $index) { <li class="prj-goal">{{ goal }}</li> }</ul>
          }
        </section>

        <section class="wrk-card" aria-labelledby="prj-h-progress">
          <h2 id="prj-h-progress">In progress <span class="wrk-count">{{ inProgress().length }}</span><span class="wrk-grow"></span>
            <a class="prj-h-link" [routerLink]="['/projects', p.key, 'work']">Open Work →</a></h2>
          @if (itemsError()) { <div class="wrk-error" role="alert">{{ itemsError() }}</div> }
          @else if (!itemsLoaded()) { <div class="wrk-empty">Loading…</div> }
          @else if (inProgress().length === 0) { <div class="wrk-empty">Nothing is in progress.</div> }
          <ul class="wrk-list">
            @for (row of inProgress(); track row.key) {
              <li class="prj-item" [attr.data-key]="row.key">
                <span class="wrk-key">{{ row.key }}</span>
                <div class="prj-item-main">
                  <a class="prj-item-title" [routerLink]="['/work/items', row.key]" [title]="row.title">{{ row.title }}</a>
                  <div class="prj-meta">{{ row.meta }} · {{ row.updatedAt | ago }}</div>
                </div>
                @if (row.awaitingHuman) {
                  <span class="wrk-chip wrk-chip-blocked" data-reason="awaiting_human">awaiting you</span>
                } @else {
                  <span class="wrk-chip" [attr.data-status]="row.status">{{ label(row.status) }}</span>
                }
              </li>
            }
          </ul>
        </section>

        <section class="wrk-card" aria-labelledby="prj-h-reviews">
          <h2 id="prj-h-reviews">Code reviews <span class="wrk-count">{{ reviews().length }}</span><span class="wrk-grow"></span>
            <a class="prj-h-link" [routerLink]="['/projects', p.key, 'code-reviews']">All code reviews →</a></h2>
          @if (!reviewsLoaded()) { <div class="wrk-empty">Loading…</div> }
          @else if (p.repositories.length === 0) { <div class="wrk-empty">This project has no repositories.</div> }
          <ul class="wrk-list">
            @for (review of reviews(); track review.repository) {
              <li [attr.data-repo]="review.repository">
                <div class="wrk-row">
                  <span class="cg-chip cg-chip-dot {{ review.chip }}" [attr.data-state]="review.state">{{ review.label }}</span>
                  <span class="wrk-sub">{{ review.repository }} · {{ review.at | ago }}</span>
                </div>
                <div class="prj-review-summary">{{ review.summary }}</div>
              </li>
            }
          </ul>
          @if (reviewsLoaded() && unreviewed().length > 0) {
            <div class="wrk-empty">Not reviewed yet: {{ unreviewed().join(', ') }}</div>
          }
        </section>
      </div>

      <div class="prj-col">
        <section class="wrk-card" aria-labelledby="prj-h-overview">
          <h2 id="prj-h-overview">Overview</h2>
          <div class="prj-stats">
            <div class="prj-stat"><div class="prj-stat-value">{{ totals().pct }}%</div><div class="prj-stat-label">finished</div></div>
            <div class="prj-stat"><div class="prj-stat-value">{{ totals().inFlight }}</div><div class="prj-stat-label">in flight</div></div>
            <div class="prj-stat"><div class="prj-stat-value">{{ openFeatures() ?? '…' }}</div><div class="prj-stat-label">open features</div></div>
          </div>
          <div class="wrk-progress prj-overview-bar" role="img" [attr.aria-label]="totals().pct + '% finished'">
            <span class="wrk-p-complete" [style.width.%]="bar().complete"></span>
            <span class="wrk-p-closed" [style.width.%]="bar().closed"></span>
            <span class="wrk-p-flight" [style.width.%]="bar().flight"></span>
          </div>
          <div class="wrk-row">
            @for (c of counts(); track c.status) { <span class="wrk-chip" [attr.data-status]="c.status">{{ label(c.status) }} {{ c.count }}</span> }
          </div>
        </section>

        <section class="wrk-card" aria-labelledby="prj-h-repos">
          <h2 id="prj-h-repos">Repositories <span class="wrk-count">{{ p.repositories.length }}</span></h2>
          @if (p.repositories.length === 0) { <div class="wrk-empty">No repositories.</div> }
          <ul class="wrk-list">
            @for (member of p.repositories; track member.repository) {
              <li class="prj-repo">
                <a class="wrk-mono" [routerLink]="['/repos', member.repository]" [title]="member.repository">{{ member.repository }}</a>
                <span class="wrk-chip">{{ member.role }}</span>
              </li>
            }
          </ul>
        </section>
      </div>
    </div>
  `
})
export class ProjectOverviewComponent {
  private readonly api = inject(WorkApiService);
  private readonly signals = inject(ProjectSignalsService);

  readonly project = input.required<ProjectView>();
  /** Bumped by the page whenever the project's change feed has news: the items and features are read again. */
  readonly revision = input(0);
  readonly editDescription = output<void>();

  readonly items = signal<ItemView[]>([]);
  readonly itemsLoaded = signal(false);
  readonly itemsError = signal('');
  readonly features = signal<FeatureView[] | null>(null);
  readonly latest = signal<{ repository: string; review: RepositoryReviewResponse | null }[]>([]);
  readonly reviewsLoaded = signal(false);

  readonly inProgress = computed(() => inProgressItems(this.items()).map(inProgressRow));
  readonly reviews = computed<ReviewRow[]>(() => this.latest().flatMap(entry => entry.review ? [reviewRow(entry.repository, entry.review)] : []));
  readonly unreviewed = computed(() => this.latest().filter(entry => !entry.review).map(entry => entry.repository));
  readonly totals = computed(() => itemTotals(this.project().item_counts));
  readonly bar = computed(() => {
    const totals = this.totals();
    return { complete: share(totals.complete, totals.total), closed: share(totals.closed, totals.total), flight: share(totals.inFlight, totals.total) };
  });
  readonly counts = computed(() =>
    Object.entries(this.project().item_counts ?? {}).filter(([, count]) => count > 0).map(([status, count]) => ({ status: status as WorkStatus, count })));
  readonly openFeatures = computed(() => {
    const features = this.features();
    return features === null ? null : features.filter(feature => feature.status === 'open').length;
  });

  private itemsGeneration = 0;
  private reviewsGeneration = 0;
  private reviewedRepos = '';

  constructor() {
    effect(() => {
      const project = this.project();
      this.revision();
      untracked(() => {
        void this.loadWork(project.key);
        const repos = `${project.key}|${project.repositories.map(member => member.repository).join(',')}`;
        if (repos !== this.reviewedRepos) {
          this.reviewedRepos = repos;
          void this.loadReviews(project.repositories.map(member => member.repository));
        }
      });
    });
  }

  label(status: WorkStatus): string {
    return statusLabel(status);
  }

  /** The project's unfinished items (in flight and waiting on a person are among them) and its features. */
  private async loadWork(key: string): Promise<void> {
    const generation = ++this.itemsGeneration;
    try {
      const [items, features] = await Promise.all([
        this.api.items(key, { status: ['backlog', 'ready', 'in_progress', 'in_review'] }),
        this.api.features(key)
      ]);
      if (generation !== this.itemsGeneration) return;
      this.items.set(items);
      this.features.set(features);
      this.itemsError.set('');
    } catch (error) {
      if (generation === this.itemsGeneration) this.itemsError.set(error instanceof Error ? error.message : String(error));
    } finally {
      if (generation === this.itemsGeneration) this.itemsLoaded.set(true);
    }
  }

  private async loadReviews(repositories: string[]): Promise<void> {
    const generation = ++this.reviewsGeneration;
    this.reviewsLoaded.set(false);
    const reviews = await this.signals.latestReviews(repositories);
    if (generation !== this.reviewsGeneration) return;
    this.latest.set(repositories.map((repository, index) => ({ repository, review: reviews[index] })));
    this.reviewsLoaded.set(true);
  }
}
