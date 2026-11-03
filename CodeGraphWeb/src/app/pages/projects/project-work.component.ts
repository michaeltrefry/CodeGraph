import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ActivityComponent } from '../work/work-activity';
import { ErrorComponent, KeyHolder, WorkPageBase } from '../work/work-base';
import { DialogComponent, FeatureFormComponent } from '../work/work-forms';
import { withDebounce } from '../work/work-live.service';
import { MessageListComponent } from '../work/work-messages-ui';
import { ResourcesComponent } from '../work/work-resources-ui';
import { SessionListComponent } from '../work/work-sessions-ui';
import { AgoPipe } from '../work/work-shared';
import { FeatureView, ItemView, MessageView, ProjectView, ResourceView, SessionView } from '../work/work.models';
import {
  DEFAULT_FEATURE_FILTER, FEATURE_SORTS, FeatureFilter, FeatureSort, FeatureStatusFilter, clearFilters, featureTable, filterFromParams,
  filterToParams, fullDate, hasFilters, shownText, sortByColumn, uncoveredLabel
} from './feature-filter';

interface Column { label: string; sort: FeatureSort | null; cls: string }

const COLUMNS: Column[] = [
  { label: 'Feature', sort: 'key', cls: 'prj-col-feature' },
  { label: 'Status', sort: null, cls: '' },
  { label: 'Progress', sort: 'pct', cls: 'prj-progress-cell' },
  { label: 'Remaining', sort: 'remaining', cls: 'prj-num' },
  { label: 'Created by', sort: null, cls: '' },
  { label: 'Created', sort: 'created', cls: '' },
  { label: 'Updated', sort: 'updated', cls: 'prj-num' }
];

/**
 * The Work tab: what waits on a person, who is working, the project's features (filtered and sorted
 * through the URL's query params), holds and waiters, and recent activity. The page owns the head and
 * the change feed; this tab reads its own data again whenever revision changes.
 */
@Component({
  selector: 'app-project-work',
  standalone: true,
  imports: [RouterLink, AgoPipe, ErrorComponent, DialogComponent, FeatureFormComponent, MessageListComponent, SessionListComponent, ResourcesComponent, ActivityComponent],
  styleUrl: './projects.scss',
  template: `
    @let p = project();
    <div class="prj-work">
      <wrk-error [error]="error()" (reload)="reload()" />
      @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }

      <!-- Live sessions is a sidebar, so however many there are the main column is not pushed down. -->
      <div class="wrk-grid-2 prj-work-layout">
        <div class="prj-work-main">
          <section class="wrk-card" [class.wrk-attention]="blockedOnYou().length > 0" aria-labelledby="prj-h-you">
            <h2 id="prj-h-you">Blocked on you <span class="wrk-count">{{ blockedOnYou().length }}</span></h2>
            <wrk-message-list [messages]="blockedOnYou()" [project]="p.key" [acknowledgeable]="true" empty="Nothing is waiting on you." (changed)="reload()" />
          </section>

          <!-- Above Features, which can run long; the activity shows only the latest few. -->
          <div class="wrk-grid">
            <section class="wrk-card" aria-labelledby="prj-h-holds">
              <h2 id="prj-h-holds">Holds and waiters</h2>
              <wrk-resources [project]="p.key" [resources]="resources()" (changed)="reload()" />
            </section>

            <section class="wrk-card" aria-labelledby="prj-h-activity">
              <h2 id="prj-h-activity">Recent activity</h2>
              <wrk-activity [entries]="feed()" [limit]="3" />
            </section>
          </div>

          <section class="wrk-card prj-features" aria-labelledby="prj-h-features">
            <div class="prj-features-head">
              <h2 id="prj-h-features">Features</h2>
              <span class="prj-shown">{{ shown() }}</span>
              <span class="wrk-grow"></span>
              <button type="button" class="wrk-btn" (click)="creating.set(true)">New feature</button>
            </div>

            <div class="prj-filters">
              <label class="wrk-input prj-search">
                <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true"><path fill-rule="evenodd" d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z" clip-rule="evenodd"/></svg>
                <span class="wrk-sr">Filter features</span>
                <input type="search" placeholder="Filter by key or title" [value]="filter().q" (input)="change({ q: $any($event.target).value })" />
              </label>

              <div class="cg-segmented" role="group" aria-label="Status">
                @for (option of statuses; track option.value) {
                  <button type="button" [class.active]="filter().status === option.value" [attr.aria-pressed]="filter().status === option.value" [attr.data-status]="option.value"
                    (click)="change({ status: option.value })">{{ option.label }} <span class="prj-seg-count">{{ table().statusCounts[option.value] }}</span></button>
                }
              </div>

              <button type="button" class="prj-pill" data-dot="blue" [attr.aria-pressed]="filter().inProg" (click)="change({ inProg: !filter().inProg })"><span class="prj-pill-dot"></span>Has items in progress</button>
              <button type="button" class="prj-pill" data-dot="amber" [attr.aria-pressed]="filter().uncovered" (click)="change({ uncovered: !filter().uncovered })"><span class="prj-pill-dot"></span>Uncovered requirements</button>

              <label class="prj-select">Created by
                <select class="wrk-input" [value]="filter().by" (change)="change({ by: $any($event.target).value })">
                  <option value="">Anyone</option>
                  @for (creator of creators(); track creator) { <option [value]="creator" [selected]="creator === filter().by">{{ creator }}</option> }
                </select>
              </label>

              <span class="wrk-grow"></span>

              <label class="prj-select">Sort
                <select class="wrk-input" [value]="filter().sort" (change)="change({ sort: $any($event.target).value })">
                  @for (option of sorts; track option.value) { <option [value]="option.value" [selected]="option.value === filter().sort">{{ option.label }}</option> }
                </select>
              </label>
              <button type="button" class="wrk-btn prj-dir" title="Reverse order" (click)="change({ dir: filter().dir === 'asc' ? 'desc' : 'asc' })">{{ filter().dir === 'asc' ? '↑ asc' : '↓ desc' }}</button>
              @if (filtered()) { <button type="button" class="wrk-btn wrk-btn-quiet prj-clear" (click)="clear()">Clear filters</button> }
            </div>

            @if (featuresError()) { <div class="wrk-error prj-features-note" role="alert">{{ featuresError() }}</div> }
            <div class="prj-scroll">
              <table class="wrk-table prj-ftable">
                <thead><tr>
                  @for (column of columns; track column.label) {
                    <th [class]="column.cls" [attr.aria-sort]="column.sort && filter().sort === column.sort ? (filter().dir === 'asc' ? 'ascending' : 'descending') : null"
                      [attr.data-active]="!!column.sort && filter().sort === column.sort">
                      @if (column.sort) {
                        <button type="button" class="prj-sort" [attr.data-sort]="column.sort" (click)="sortBy(column.sort)">{{ column.label }}@if (filter().sort === column.sort) {<span class="prj-arrow">{{ filter().dir === 'asc' ? '↑' : '↓' }}</span>}</button>
                      } @else { {{ column.label }} }
                    </th>
                  }
                </tr></thead>
                <tbody>
                  @for (row of table().rows; track row.key) {
                    <tr [attr.data-key]="row.key">
                      <td class="prj-col-feature">
                        <div class="prj-feature-line">
                          <span class="wrk-key">{{ row.key }}</span>
                          <a class="prj-feature-title" [routerLink]="['/work/features', row.key]">{{ row.title }}</a>
                        </div>
                        @if (row.uncovered > 0 || row.blocked > 0) {
                          <div class="prj-flags">
                            @if (row.uncovered > 0) { <span class="cg-chip cg-chip-warn" data-flag="uncovered">{{ uncovered(row.uncovered) }}</span> }
                            @if (row.blocked > 0) { <span class="wrk-chip wrk-chip-blocked" data-flag="blocked">{{ row.blocked }} blocked</span> }
                          </div>
                        }
                      </td>
                      <td><span class="wrk-chip" [attr.data-status]="row.status === 'complete' ? 'complete' : 'in_progress'">{{ row.status }}</span></td>
                      <td class="prj-progress-cell">
                        <div class="prj-progress">
                          <div class="wrk-progress" role="img" [attr.aria-label]="row.pct + '% finished'">
                            <span class="wrk-p-complete" [style.width.%]="row.bar.complete"></span>
                            <span class="wrk-p-closed" [style.width.%]="row.bar.closed"></span>
                            <span class="wrk-p-flight" [style.width.%]="row.bar.flight"></span>
                          </div>
                          <span class="prj-pct">{{ row.pct }}%</span>
                        </div>
                      </td>
                      <td class="prj-num prj-remaining">{{ row.remaining }} <span class="prj-faint">/ {{ row.total }}</span></td>
                      <td class="prj-by">{{ row.createdBy }}</td>
                      <td class="prj-when" [title]="full(row.createdAt)">{{ row.createdAt | ago }}</td>
                      <td class="prj-when prj-num" [title]="full(row.updatedAt)">{{ row.updatedAt | ago }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (!featuresLoaded()) {
              <div class="prj-features-empty">Loading…</div>
            } @else if (table().total === 0) {
              <div class="prj-features-empty">No features yet. Create one, or import a plan.</div>
            } @else if (table().rows.length === 0) {
              <div class="prj-features-empty">No features match these filters. <button type="button" class="wrk-btn wrk-btn-quiet" (click)="clear()">Clear filters</button></div>
            }
            <div class="prj-legend" aria-label="Progress legend">
              <span><i data-k="complete"></i>complete</span>
              <span><i data-k="closed"></i>closed</span>
              <span><i data-k="flight"></i>in progress / in review</span>
            </div>
          </section>
        </div>

        <aside class="prj-work-side">
          <section class="wrk-card" aria-labelledby="prj-h-sessions">
            <h2 id="prj-h-sessions">Live sessions <span class="wrk-count">{{ sessions().length }}</span></h2>
            <wrk-session-list [sessions]="sessions()" [claimed]="claimed()" [feed]="feed()" />
          </section>
        </aside>
      </div>
    </div>

    @if (creating()) {
      <wrk-dialog heading="New feature" (closed)="creating.set(false)">
        <wrk-error [error]="error()" [reloadable]="false" />
        <wrk-feature-form (saved)="createFeature($event)" (cancelled)="creating.set(false)" />
      </wrk-dialog>
    }
  `
})
export class ProjectWorkComponent extends WorkPageBase implements OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly project = input.required<ProjectView>();
  /** Bumped by the page whenever the project's change feed has news: everything here is read again. */
  readonly revision = input(0);

  readonly columns = COLUMNS;
  readonly sorts = FEATURE_SORTS;
  readonly statuses: { value: FeatureStatusFilter; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'open', label: 'Open' },
    { value: 'complete', label: 'Complete' }
  ];

  readonly sessions = signal<SessionView[]>([]);
  readonly resources = signal<ResourceView[]>([]);
  readonly blockedOnYou = signal<MessageView[]>([]);
  readonly claimed = signal<ItemView[]>([]);
  readonly features = signal<FeatureView[]>([]);
  readonly featuresLoaded = signal(false);
  readonly featuresError = signal('');
  readonly creating = signal(false);

  readonly filter = signal<FeatureFilter>({ ...DEFAULT_FEATURE_FILTER });
  readonly table = computed(() => featureTable(this.features(), this.filter()));
  readonly creators = computed(() => {
    const creators = this.table().creators;
    const by = this.filter().by;
    // A creator from a shared link stays selectable even when none of the features is theirs.
    return by && !creators.includes(by) ? [...creators, by] : creators;
  });
  readonly filtered = computed(() => hasFilters(this.filter()));
  readonly shown = computed(() => shownText(this.table().rows.length, this.table().total));
  readonly uncovered = uncoveredLabel;
  readonly full = fullDate;

  private readonly featureKey = new KeyHolder('ui-feature');
  private readonly refresh = withDebounce(() => void this.reload(), 300);
  private stopStream?: () => void;
  private streamed = '';
  private generation = 0;
  /** Navigations of our own in flight: the query params they emit are already in filter. */
  private writing = 0;

  constructor() {
    super();
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(params => {
      if (this.writing === 0) this.filter.set(filterFromParams(params));
    });
    effect(() => {
      const project = this.project();
      this.revision();
      untracked(() => {
        if (project.key !== this.streamed) this.switchTo(project.key);
        void this.reload();
      });
    });
  }

  ngOnDestroy(): void {
    this.stopStream?.();
    this.refresh.cancel();
  }

  /** Applies a filter or sort change and writes it to the URL (replacing the entry, so typing is one history step). */
  change(patch: Partial<FeatureFilter>): void {
    this.apply({ ...this.filter(), ...patch });
  }

  sortBy(column: FeatureSort): void {
    this.apply(sortByColumn(this.filter(), column));
  }

  clear(): void {
    this.apply(clearFilters(this.filter()));
  }

  private apply(filter: FeatureFilter): void {
    this.filter.set(filter);
    this.writing++;
    void this.router.navigate([], { relativeTo: this.route, queryParams: filterToParams(filter), replaceUrl: true })
      .catch(() => false)
      .finally(() => this.writing--);
  }

  /** A new project: drop the last one's data and follow its messages (Blocked on you is not in the change feed). */
  private switchTo(key: string): void {
    this.stopStream?.();
    this.streamed = key;
    this.sessions.set([]);
    this.resources.set([]);
    this.blockedOnYou.set([]);
    this.claimed.set([]);
    this.features.set([]);
    this.featuresLoaded.set(false);
    this.featuresError.set('');
    this.feed.set([]);
    this.creating.set(false);
    this.featureKey.reset();
    this.stopStream = this.live.streamMessages(key, () => this.refresh.call(), undefined, () => this.refresh.call());
  }

  protected async reload(): Promise<void> {
    const key = this.project().key;
    const generation = ++this.generation;
    await Promise.all([this.loadFeatures(key, generation), this.loadNow(key, generation)]);
  }

  private async loadFeatures(key: string, generation: number): Promise<void> {
    try {
      const features = await this.api.features(key);
      if (generation !== this.generation) return;
      this.features.set(features);
      this.featuresError.set('');
    } catch (error) {
      if (generation === this.generation) this.featuresError.set(error instanceof Error ? error.message : String(error));
    } finally {
      if (generation === this.generation) this.featuresLoaded.set(true);
    }
  }

  private async loadNow(key: string, generation: number): Promise<void> {
    try {
      const [sessions, resources, messages, claimed] = await Promise.all([
        this.api.sessions(key),
        this.api.resources(key),
        this.api.messages(key, { open: true, toHuman: true, limit: 50 }),
        this.api.items(key, { claimed: true, status: ['ready', 'in_progress', 'in_review', 'backlog'] }),
        this.loadRecent(key, 40)
      ]);
      if (generation !== this.generation) return;
      this.error.set(null);
      this.sessions.set(sessions);
      this.resources.set(resources);
      this.blockedOnYou.set(messages.items);
      this.claimed.set(claimed);
    } catch (error) {
      if (generation === this.generation) await this.run(() => Promise.reject(error));
    }
  }

  async createFeature(fields: Record<string, unknown>): Promise<void> {
    const created = await this.run(() => this.api.create({ project: this.project().key, feature: fields, idempotency_key: this.featureKey.key }), 'Feature created.');
    this.noteReplay(created);
    if (created) {
      this.featureKey.reset();
      this.creating.set(false);
      await this.reload();
    }
  }
}
