import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ErrorComponent, KeyHolder, WorkContext, WorkPageBase } from '../work/work-base';
import { DialogComponent, ItemFormComponent, ProjectFormComponent } from '../work/work-forms';
import { AgoPipe } from '../work/work-shared';
import { ProjectView } from '../work/work.models';
import { PROJECT_TABS, ProjectSignalsService, ProjectTab, isProjectTab, itemTotals } from './project-data';
import { ProjectOverviewComponent } from './project-overview.component';

/**
 * A project page: /projects/:key/:tab. The project comes from the route key (never from
 * WorkContext.selected()); the head, the subline and the tab bar are shared by every tab, and each tab
 * is a component given the project and a revision that is bumped whenever the change feed has news.
 */
@Component({
  selector: 'app-project-page',
  standalone: true,
  imports: [FormsModule, RouterLink, AgoPipe, ErrorComponent, DialogComponent, ItemFormComponent, ProjectFormComponent, ProjectOverviewComponent],
  styleUrl: './projects.scss',
  template: `
    <div class="wrk-page">
      <wrk-error [error]="error()" (reload)="reload()" />
      @if (project(); as p) {
        <div class="wrk-head">
          <div class="prj-title">
            <div class="wrk-key">{{ p.key }} · v{{ p.version }}</div>
            <h1>{{ p.name }}</h1>
          </div>
          <span class="wrk-live" [attr.data-state]="feedError() ? 'off' : 'on'" [title]="feedError() || 'Following the change feed'">{{ feedError() ? 'reconnecting' : 'live' }}</span>
          <span class="wrk-grow"></span>
          <div class="prj-actions">
            <button type="button" class="wrk-btn" [disabled]="busy()" (click)="claimNext()">Claim next item</button>
            <button type="button" class="wrk-btn" (click)="dialog.set('plan')">Import plan</button>
            <button type="button" class="wrk-btn" (click)="dialog.set('edit')">Edit project</button>
            <button type="button" class="wrk-btn wrk-btn-primary" (click)="dialog.set('item')">New item</button>
          </div>
        </div>
        @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }
        <p class="wrk-sub prj-subline">Created by {{ p.created_by }} · {{ p.repositories.length }} {{ p.repositories.length === 1 ? 'repository' : 'repositories' }} · updated {{ p.updated_at | ago }}</p>

        <nav class="wrk-tabs" role="tablist" aria-label="Project sections">
          @for (t of tabs; track t.id) {
            <a class="wrk-tab" role="tab" [routerLink]="['/projects', p.key, t.id]" [attr.aria-selected]="tab() === t.id" [attr.data-tab]="t.id">
              <span>{{ t.label }}</span>
              @if (t.id === 'work') { <span class="prj-tab-count" [attr.aria-label]="workCount() + ' in progress'">{{ workCount() }}</span> }
              @if (t.id === 'approvals' && approvals() !== null) { <span class="prj-tab-count" [attr.aria-label]="approvals() + ' open approvals'">{{ approvals() }}</span> }
            </a>
          }
        </nav>

        <div role="tabpanel" [attr.aria-label]="tabLabel()">
          @switch (tab()) {
            @case ('overview') {
              <app-project-overview [project]="p" [revision]="revision()" (editDescription)="dialog.set('edit')" />
            }
            @case ('work') {
              <!-- TAB: work -->
              <div class="wrk-card"><div class="wrk-empty">The Work tab is not built yet. Use the cross-project <a routerLink="/work" (click)="ctx.select(p.key)">Work</a> page meanwhile.</div></div>
            }
            @case ('approvals') {
              <!-- TAB: approvals -->
              <div class="wrk-card"><div class="wrk-empty">The Approvals tab is not built yet. Use the cross-project <a routerLink="/approvals">Approvals</a> page meanwhile.</div></div>
            }
            @case ('knowledge') {
              <!-- TAB: knowledge -->
              <div class="wrk-card"><div class="wrk-empty">The Knowledge tab is not built yet. Use the cross-project <a routerLink="/knowledge">Knowledge</a> page meanwhile.</div></div>
            }
            @case ('lessons') {
              <!-- TAB: lessons -->
              <div class="wrk-card"><div class="wrk-empty">The Lessons tab is not built yet. Use the cross-project <a routerLink="/lessons">Lessons</a> page meanwhile.</div></div>
            }
            @case ('memory') {
              <!-- TAB: memory -->
              <div class="wrk-card"><div class="wrk-empty">The Memory tab is not built yet. Use the cross-project <a routerLink="/memory">Memory</a> page meanwhile.</div></div>
            }
            @case ('dreaming') {
              <!-- TAB: dreaming -->
              <div class="wrk-card"><div class="wrk-empty">The Dreaming tab is not built yet. Use the cross-project <a routerLink="/dreaming">Dreaming</a> page meanwhile.</div></div>
            }
          }
        </div>

        @switch (dialog()) {
          @case ('edit') {
            <wrk-dialog heading="Edit project" (closed)="dialog.set('')">
              <wrk-error [error]="error()" (reload)="reload()" />
              <wrk-project-form [initial]="p" (saved)="saveProject($event)" (cancelled)="dialog.set('')" />
            </wrk-dialog>
          }
          @case ('item') {
            <wrk-dialog heading="New item" (closed)="dialog.set('')">
              <wrk-error [error]="error()" [reloadable]="false" />
              <wrk-item-form (saved)="createItem($event)" (cancelled)="dialog.set('')" />
            </wrk-dialog>
          }
          @case ('plan') {
            <wrk-dialog heading="Import a plan" (closed)="dialog.set('')">
              <p class="wrk-sub">A plan is all-or-nothing: <code>{{ '{' }}"features": [{{ '{' }}"ref", "title", ...{{ '}' }}], "items": [{{ '{' }}"ref", "title", "feature", "blocked_by": ["ref"], ...{{ '}' }}]{{ '}' }}</code>. At most 200 items and 20 features.</p>
              <textarea class="wrk-input" rows="14" aria-label="Plan JSON" [(ngModel)]="planJson" spellcheck="false"></textarea>
              @if (planError()) { <div class="wrk-error">{{ planError() }}</div> }
              <wrk-error [error]="error()" [reloadable]="false" />
              <div class="wrk-dialog-actions">
                <button type="button" class="wrk-btn" (click)="dialog.set('')">Cancel</button>
                <button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !planJson.trim()" (click)="importPlan()">Import</button>
              </div>
            </wrk-dialog>
          }
        }
      } @else if (!error()) {
        <div class="wrk-empty">Loading…</div>
      }
    </div>
  `
})
export class ProjectPageComponent extends WorkPageBase implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly signals = inject(ProjectSignalsService);
  readonly ctx = inject(WorkContext);
  readonly tabs = PROJECT_TABS;
  readonly key = signal('');
  readonly tab = signal<ProjectTab>('overview');
  readonly project = signal<ProjectView | null>(null);
  /** Open approval items of the project; null until counted (or when they cannot be). */
  readonly approvals = signal<number | null>(null);
  /** Bumped on every reload, so the tab re-reads its own data when the change feed has news. */
  readonly revision = signal(0);
  readonly workCount = computed(() => itemTotals(this.project()?.item_counts).inFlight);
  readonly tabLabel = computed(() => PROJECT_TABS.find(tab => tab.id === this.tab())?.label ?? '');
  readonly dialog = signal<'' | 'edit' | 'item' | 'plan'>('');
  readonly planError = signal('');
  planJson = '';
  private readonly keys = { item: new KeyHolder('ui-item'), plan: new KeyHolder('ui-plan') };

  constructor() {
    super();
  }

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      const key = params.get('key') ?? '';
      const tab = params.get('tab');
      if (!isProjectTab(tab)) {
        void this.router.navigate(['/projects', key, 'overview'], { replaceUrl: true });
        return;
      }
      this.tab.set(tab);
      if (key !== this.key()) this.open(key);
    });
  }

  /** Shows the project of the route (the component is reused when only the key changes) with a clean slate. */
  private open(key: string): void {
    this.unfollow();
    this.key.set(key);
    this.project.set(null);
    this.approvals.set(null);
    this.dialog.set('');
    this.error.set(null);
    this.notice.set('');
    if (!key) return;
    this.follow(key);
    void this.reload();
  }

  protected async reload(): Promise<void> {
    const key = this.key();
    if (!key) return;
    try {
      const [project, approvals] = await Promise.all([
        this.api.get<ProjectView>(key),
        this.signals.openApprovals(key).catch(() => null)
      ]);
      if (key !== this.key()) return;
      this.project.set(project);
      this.approvals.set(approvals);
      this.revision.update(revision => revision + 1);
    } catch (error) {
      await this.run(() => Promise.reject(error));
    }
  }

  async saveProject(fields: Record<string, unknown>): Promise<void> {
    const project = this.project()!;
    if (await this.run(() => this.api.update(project.key, { expected_version: project.version, ...fields }), 'Project saved.')) {
      this.dialog.set('');
      await this.reload();
      // The breadcrumb and the projects list read the name from the shared context.
      void this.ctx.load();
    }
  }

  async createItem(fields: Record<string, unknown>): Promise<void> {
    const created = await this.run(() => this.api.create({ project: this.project()!.key, item: fields, idempotency_key: this.keys.item.key }), 'Item created.');
    this.noteReplay(created);
    if (created) {
      this.keys.item.reset();
      this.dialog.set('');
      await this.reload();
    }
  }

  async importPlan(): Promise<void> {
    this.planError.set('');
    let plan: unknown;
    try {
      plan = JSON.parse(this.planJson);
    } catch (error) {
      this.planError.set(`That is not valid JSON: ${error instanceof Error ? error.message : error}`);
      return;
    }
    const result = await this.run(() => this.api.create({ project: this.project()!.key, plan, idempotency_key: this.keys.plan.key }), 'Plan imported.');
    this.noteReplay(result);
    if (result) {
      this.keys.plan.reset();
      this.dialog.set('');
      this.planJson = '';
      await this.reload();
    }
  }

  async claimNext(): Promise<void> {
    const result = await this.run(() => this.api.next({ project: this.project()!.key, marker: 'ui' }));
    if (result?.item) {
      this.notice.set(`${result.reused ? 'You already hold' : 'Claimed'} ${result.item.key}: ${result.item.title}`);
      await this.router.navigate(['/work/items', result.item.key]);
    } else if (result) {
      this.notice.set('There is no ready work to claim.');
    }
  }
}
