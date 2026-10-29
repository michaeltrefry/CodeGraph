import { Component, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ActivityComponent } from './work-activity';
import { ErrorComponent, KeyHolder, WorkPageBase } from './work-base';
import { DependencyGraphComponent } from './work-dependency-graph.component';
import { DialogComponent, FeatureFormComponent, ItemFormComponent } from './work-forms';
import { featureProgress } from './work-graph';
import { SavePointsComponent } from './work-savepoints';
import { ActorComponent, AgoPipe, StatusComponent } from './work-shared';
import { FeatureView, ItemView, PlanPreview, WORK_STATUSES, WorkApiError, WorkStatus } from './work.models';

type Tab = 'board' | 'graph' | 'progress' | 'docs' | 'plan';

/** Feature: board with drag to move, dependency graph, progress, and re-planning (sync_plan). */
@Component({
  selector: 'app-work-feature',
  standalone: true,
  imports: [FormsModule, RouterLink, ActorComponent, AgoPipe, StatusComponent, ErrorComponent, DependencyGraphComponent, DialogComponent, FeatureFormComponent, ItemFormComponent, ActivityComponent, SavePointsComponent],
  template: `
    <div class="wrk-page">
      @if (feature(); as f) {
        <div class="wrk-head">
          <div>
            <div class="wrk-key"><a [routerLink]="['/work/projects', f.project_key]">{{ f.project_key }}</a> · {{ f.key }} · v{{ f.version }}</div>
            <h1>{{ f.title }}</h1>
          </div>
          <span class="wrk-chip" [attr.data-status]="f.status === 'complete' ? 'complete' : 'in_progress'">{{ f.status }}</span>
          <span class="wrk-live" [attr.data-state]="feedError() ? 'off' : 'on'">{{ feedError() ? 'reconnecting' : 'live' }}</span>
          <span class="wrk-grow"></span>
          <button type="button" class="wrk-btn" (click)="dialog.set('item')">New item</button>
          <button type="button" class="wrk-btn" (click)="dialog.set('edit')">Edit feature</button>
        </div>
        <wrk-error [error]="error()" (reload)="reload()" />
        @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }

        <div class="wrk-tabs" role="tablist" aria-label="Feature views">
          @for (t of tabs; track t.id) {
            <button type="button" role="tab" class="wrk-tab" [attr.aria-selected]="tab() === t.id" (click)="tab.set(t.id)">{{ t.label }}</button>
          }
        </div>

        @if (tab() === 'board' || tab() === 'graph') {
          <div class="wrk-filters" role="search" aria-label="Filter items">
            <input class="wrk-input" aria-label="Repository" placeholder="Repository" [(ngModel)]="fRepository" (ngModelChange)="filtersChanged()" />
            <select class="wrk-input" aria-label="Type" [(ngModel)]="fType" (ngModelChange)="filtersChanged()"><option value="">any type</option><option>story</option><option>task</option><option>bug</option></select>
            <input class="wrk-input" aria-label="Label" placeholder="Label" [(ngModel)]="fLabel" (ngModelChange)="filtersChanged()" />
            <input class="wrk-input" aria-label="Claimed by" placeholder="Claimed by" [(ngModel)]="fClaimed" (ngModelChange)="filtersChanged()" />
            <span class="wrk-sub">{{ filtered().length }} of {{ items().length }} items</span>
          </div>
        }

        @switch (tab()) {
          @case ('board') {
            <div class="wrk-board" role="group" aria-label="Board">
              @for (status of statuses; track status) {
                <section class="wrk-col" [attr.data-over]="over() === status ? 'true' : null" (dragover)="dragOver($event, status)" (dragleave)="over.set('')" (drop)="drop($event, status)" [attr.aria-label]="status + ' column'">
                  <div class="wrk-col-head"><span>{{ status.replace('_', ' ') }}</span><span>{{ column(status).length }}</span></div>
                  <div class="wrk-col-body">
                    @for (item of column(status); track item.key) {
                      <article class="wrk-tile" draggable="true" [attr.data-blocked]="item.blocked ? 'true' : null" [attr.data-reason]="item.blocked_reason ?? null" [attr.data-moving]="moving() === item.key ? 'true' : null"
                        (dragstart)="dragStart($event, item)" (dragend)="over.set('')">
                        <div class="wrk-row"><a [routerLink]="['/work/items', item.key]">{{ item.key }}</a>
                          <span class="wrk-chip">{{ item.priority }}</span>
                          @if (item.blocked) { <wrk-status [status]="item.status" [blocked]="true" [reason]="item.blocked_reason" /> }</div>
                        <div>{{ item.title }}</div>
                        @if (item.claim) { <div class="wrk-sub">claimed by <wrk-actor [actor]="{ name: item.claim.holder, session_id: item.claim.session_id }" /></div> }
                        <label class="wrk-sr" [attr.for]="'move-' + item.key">Move {{ item.key }} to</label>
                        <select class="wrk-input" [id]="'move-' + item.key" [value]="item.status" (change)="move(item, $any($event.target).value, $any($event.target))" style="font-size:11px;padding:2px 4px">
                          @for (s of statuses; track s) { <option [value]="s" [selected]="s === item.status">{{ s.replace('_', ' ') }}</option> }
                        </select>
                      </article>
                    }
                  </div>
                </section>
              }
            </div>
          }
          @case ('graph') {
            <wrk-dependency-graph [items]="filtered()" (selected)="openItem($event)" />
          }
          @case ('progress') {
            <div class="wrk-grid">
              <section class="wrk-card" aria-labelledby="wrk-h-prog">
                <h2 id="wrk-h-prog">Progress</h2>
                <div class="wrk-progress" role="img" [attr.aria-label]="progress().percent + '% finished'">
                  <span class="wrk-p-complete" [style.width.%]="pct(progress().complete)"></span>
                  <span class="wrk-p-closed" [style.width.%]="pct(progress().closed)"></span>
                  <span class="wrk-p-flight" [style.width.%]="pct(progress().inFlight)"></span>
                </div>
                <p>{{ progress().complete + progress().closed }} of {{ progress().total }} items finished ({{ progress().percent }}%): {{ progress().complete }} complete, {{ progress().closed }} closed, {{ progress().inFlight }} in flight.</p>
                @if (f.completion?.uncovered_requirements?.length) { <div class="wrk-conflict">Requirements not yet covered by a complete item: {{ f.completion!.uncovered_requirements!.join(', ') }}.</div> }
              </section>
              <section class="wrk-card" aria-labelledby="wrk-h-bl">
                <h2 id="wrk-h-bl">Blocked and why <span class="wrk-count">{{ progress().blocked.length }}</span></h2>
                @if (progress().blocked.length === 0) { <div class="wrk-empty">Nothing is blocked.</div> }
                <ul class="wrk-list">
                  @for (b of progress().blocked; track b.key) {
                    <li><div class="wrk-row"><a [routerLink]="['/work/items', b.key]">{{ b.key }}</a><wrk-status status="backlog" [blocked]="true" [reason]="b.reason" /><span>{{ b.title }}</span></div><div class="wrk-sub">{{ b.detail }}</div></li>
                  }
                </ul>
              </section>
              <section class="wrk-card" aria-labelledby="wrk-h-req">
                <h2 id="wrk-h-req">Requirements and acceptance tests</h2>
                @for (r of f.requirements ?? []; track r.id) {
                  <div class="wrk-row"><strong>{{ r.id }}</strong><span class="wrk-grow">{{ r.text }}</span>
                    <span class="wrk-chip" [attr.data-status]="r.state === 'dropped' ? 'closed' : r.covered_by?.length ? 'complete' : 'backlog'">{{ r.state === 'dropped' ? 'dropped' : r.covered_by?.length ? 'covered by ' + r.covered_by!.join(', ') : 'uncovered' }}</span></div>
                }
                @if ((f.requirements ?? []).length === 0) { <div class="wrk-empty">No requirements.</div> }
                @if ((f.acceptance_tests ?? []).length > 0) { <h3 style="margin-top:10px">Acceptance tests</h3><ul>@for (t of f.acceptance_tests; track t) { <li>{{ t }}</li> }</ul> }
              </section>
              <section class="wrk-card" aria-labelledby="wrk-h-fa"><h2 id="wrk-h-fa">Recent activity</h2><wrk-activity [entries]="featureFeed()" [limit]="15" /></section>
            </div>
          }
          @case ('docs') {
            <section class="wrk-card" aria-labelledby="wrk-h-docs">
              <h2 id="wrk-h-docs">Documents <span class="wrk-count">{{ f.documents.length }}</span></h2>
              @if (f.documents.length === 0) { <div class="wrk-empty">No documents.</div> }
              @for (doc of f.documents; track doc.id) {
                <article class="wrk-msg" style="margin-bottom:8px" data-testid="document">
                  @if (editingDoc() === doc.id) {
                    <div class="wrk-form">
                      <input class="wrk-input" aria-label="Document title" [(ngModel)]="docTitle" />
                      <textarea class="wrk-input" rows="8" aria-label="Document body" [(ngModel)]="docBody"></textarea>
                      <div class="wrk-row"><button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !docTitle.trim()" (click)="saveDocument(doc.id)">Save document</button>
                        <button type="button" class="wrk-btn wrk-btn-quiet" (click)="editingDoc.set('')">Cancel</button></div>
                    </div>
                  } @else {
                    <div class="wrk-row"><strong class="wrk-grow">{{ doc.title }}</strong><button type="button" class="wrk-btn" (click)="editDocument(doc)">Edit</button></div>
                    <div class="wrk-pre">{{ doc.body }}</div>
                  }
                </article>
              }
              <details class="wrk-acquire" style="margin-top:10px">
                <summary>Add a document</summary>
                <form class="wrk-form" (ngSubmit)="addDocument()">
                  <label>Title <input class="wrk-input" name="ntitle" [(ngModel)]="newDocTitle" /></label>
                  <label>Body <textarea class="wrk-input" name="nbody" rows="6" [(ngModel)]="newDocBody"></textarea></label>
                  <div class="wrk-row"><button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !newDocTitle.trim() || !newDocBody.trim()">Add document</button></div>
                </form>
              </details>
            </section>
            <section class="wrk-card" style="margin-top:14px" aria-labelledby="wrk-h-fsp">
              <h2 id="wrk-h-fsp">Save points</h2>
              <wrk-save-points (saved)="savePoint($event)" />
            </section>
          }
          @case ('plan') {
            <section class="wrk-card" aria-labelledby="wrk-h-plan">
              <h2 id="wrk-h-plan">Re-plan this feature (sync_plan)</h2>
              <p class="wrk-sub">Edit the revised plan, preview what it would change, then confirm. Items match by ref or key; a field left out is unchanged; items left out are closed unless finished.</p>
              <div class="wrk-row"><button type="button" class="wrk-btn" (click)="fillPlan()">Fill from the current feature</button></div>
              <textarea class="wrk-input" rows="14" aria-label="Revised plan JSON" spellcheck="false" [(ngModel)]="planJson" style="margin-top:8px"></textarea>
              @if (planError()) { <div class="wrk-error">{{ planError() }}</div> }
              <div class="wrk-row" style="margin-top:8px"><button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !planJson.trim()" (click)="preview()">Preview changes</button></div>
              @if (previewed(); as p) {
                <div class="wrk-card" style="margin-top:12px" data-testid="plan-preview">
                  <h3>Preview</h3>
                  @if (p.unchanged) { <div class="wrk-empty">The plan changes nothing.</div> }
                  @for (section of previewSections(p); track section.title) {
                    @if (section.rows.length > 0) { <h3 style="margin-top:8px">{{ section.title }}</h3><ul>@for (row of section.rows; track row) { <li>{{ row }}</li> }</ul> }
                  }
                  @for (w of p.warnings ?? []; track w) { <div class="wrk-conflict">{{ w }}</div> }
                  @for (pivot of p.pivots ?? []; track pivot.key) { <div class="wrk-conflict">Pivot: {{ pivot.key }} ({{ pivot.action }})@if (pivot.holder) { , held by <wrk-actor [actor]="pivot.holder" /> }</div> }
                  <div class="wrk-sub">Valid until {{ p.expires_at | ago }}.</div>
                  <div class="wrk-dialog-actions"><button type="button" class="wrk-btn" (click)="previewed.set(null)">Discard</button>
                    <button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || p.unchanged" (click)="confirmPlan(p)">Confirm and apply</button></div>
                </div>
              }
            </section>
          }
        }

        @switch (dialog()) {
          @case ('edit') { <wrk-dialog heading="Edit feature" (closed)="dialog.set('')"><wrk-feature-form [initial]="f" (saved)="saveFeature($event)" (cancelled)="dialog.set('')" /></wrk-dialog> }
          @case ('item') { <wrk-dialog heading="New item" (closed)="dialog.set('')"><wrk-item-form [featureKey]="f.key" (saved)="createItem($event)" (cancelled)="dialog.set('')" /></wrk-dialog> }
        }
      } @else {
        <wrk-error [error]="error()" (reload)="reload()" />
        @if (!error()) { <div class="wrk-empty">Loading…</div> }
      }
    </div>
  `
})
export class WorkFeatureComponent extends WorkPageBase implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly statuses = WORK_STATUSES;
  readonly tabs: { id: Tab; label: string }[] = [
    { id: 'board', label: 'Board' },
    { id: 'graph', label: 'Dependency graph' },
    { id: 'progress', label: 'Progress' },
    { id: 'docs', label: 'Documents' },
    { id: 'plan', label: 'Re-plan' }
  ];
  readonly tab = signal<Tab>('board');
  readonly key = signal('');
  readonly editingDoc = signal('');
  private readonly points = viewChild(SavePointsComponent);
  private readonly itemKey = new KeyHolder('ui-item');
  docTitle = '';
  docBody = '';
  newDocTitle = '';
  newDocBody = '';
  readonly feature = signal<FeatureView | null>(null);
  readonly items = signal<ItemView[]>([]);
  readonly over = signal('');
  readonly moving = signal('');
  readonly dialog = signal<'' | 'edit' | 'item'>('');
  readonly previewed = signal<PlanPreview | null>(null);
  readonly planError = signal('');
  planJson = '';
  fRepository = '';
  fType = '';
  fLabel = '';
  fClaimed = '';
  private readonly filterTick = signal(0);

  readonly filtered = computed(() => {
    this.filterTick();
    const repository = this.fRepository.trim().toLowerCase();
    const label = this.fLabel.trim().toLowerCase();
    const claimed = this.fClaimed.trim().toLowerCase();
    return this.items().filter(item =>
      (!repository || (item.repository ?? '').toLowerCase().includes(repository)) &&
      (!this.fType || item.type === this.fType) &&
      (!label || item.labels.some(candidate => candidate.toLowerCase().includes(label))) &&
      (!claimed || (item.claim?.holder ?? '').toLowerCase().includes(claimed)));
  });
  readonly progress = computed(() => featureProgress(this.items()));
  readonly featureFeed = computed(() => {
    const key = this.feature()?.key;
    const keys = new Set(this.items().map(item => item.key));
    return this.feed().filter(change => change.entity_key === key || keys.has(change.entity_key));
  });

  constructor() {
    super();
  }

  ngOnInit(): void {
    this.onKey(this.route, key => this.open(key));
  }

  /** Shows the feature of the route with a clean slate (the component is reused when only the key changes). */
  private open(key: string): void {
    this.unfollow();
    this.key.set(key);
    this.feature.set(null);
    this.items.set([]);
    this.feed.set([]);
    this.previewed.set(null);
    this.planJson = '';
    this.dialog.set('');
    this.editingDoc.set('');
    this.error.set(null);
    this.notice.set('');
    void this.reload().then(() => {
      const project = this.feature()?.project_key;
      if (project && key === this.key()) this.follow(project);
    });
  }

  ngOnDestroy(): void {
    this.unfollow();
  }

  filtersChanged(): void {
    this.filterTick.update(value => value + 1);
  }

  column(status: WorkStatus): ItemView[] {
    return this.filtered().filter(item => item.status === status).sort((a, b) => a.order - b.order || a.key.localeCompare(b.key, undefined, { numeric: true }));
  }

  pct(count: number): number {
    const total = this.progress().total;
    return total === 0 ? 0 : (count / total) * 100;
  }

  protected async reload(): Promise<void> {
    const key = this.key();
    if (!key) return;
    try {
      const feature = await this.api.get<FeatureView>(key);
      const [items] = await Promise.all([this.api.items(feature.project_key, { feature: feature.key }), this.loadRecent(feature.project_key, 60)]);
      if (key !== this.key()) return;
      this.feature.set(feature);
      this.items.set(items);
    } catch (error) {
      this.error.set(error instanceof WorkApiError ? error : new WorkApiError(0, 'error', String(error)));
    }
  }

  openItem(key: string): void {
    void this.router.navigate(['/work/items', key]);
  }

  // ---- drag to move ----------------------------------------------------------------------------

  dragStart(event: DragEvent, item: ItemView): void {
    event.dataTransfer?.setData('text/plain', item.key);
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  dragOver(event: DragEvent, status: string): void {
    event.preventDefault();
    this.over.set(status);
  }

  async drop(event: DragEvent, status: WorkStatus): Promise<void> {
    event.preventDefault();
    this.over.set('');
    const key = event.dataTransfer?.getData('text/plain');
    const item = this.items().find(candidate => candidate.key === key);
    if (item) await this.move(item, status);
  }

  /** update_work with expected_version: a conflict is shown with the current state, and the board reloads. */
  async move(item: ItemView, status: WorkStatus, select?: HTMLSelectElement): Promise<void> {
    if (item.status === status) return;
    this.moving.set(item.key);
    const result = await this.run(() => this.api.update(item.key, { expected_version: item.version, status }), `${item.key} moved to ${status.replace('_', ' ')}.`);
    this.moving.set('');
    // A refused move leaves the select showing the item's real status, not the one that was tried.
    if (!result && select) select.value = item.status;
    if (result?.item) this.items.update(items => items.map(candidate => (candidate.key === item.key ? result.item! : candidate)));
    else await this.reload();
  }

  // ---- writes ----------------------------------------------------------------------------------

  async saveFeature(fields: Record<string, unknown>): Promise<void> {
    const feature = this.feature()!;
    if (await this.run(() => this.api.update(feature.key, { expected_version: feature.version, ...fields }), 'Feature saved.')) {
      this.dialog.set('');
      await this.reload();
    }
  }

  async createItem(fields: Record<string, unknown>): Promise<void> {
    const feature = this.feature()!;
    const created = await this.run(() => this.api.create({ project: feature.project_key, item: { ...fields, feature: feature.key }, idempotency_key: this.itemKey.key }), 'Item created.');
    this.noteReplay(created);
    if (created) {
      this.itemKey.reset();
      this.dialog.set('');
      await this.reload();
    }
  }

  editDocument(doc: { id: string; title: string; body: string }): void {
    this.docTitle = doc.title;
    this.docBody = doc.body;
    this.editingDoc.set(doc.id);
  }

  /** update_work update_documents: only what changed. */
  async saveDocument(id: string): Promise<void> {
    const feature = this.feature()!;
    const before = feature.documents.find(doc => doc.id === id);
    const patch: Record<string, unknown> = { id };
    if (before?.title !== this.docTitle.trim()) patch['title'] = this.docTitle.trim();
    if (before?.body !== this.docBody) patch['body'] = this.docBody;
    if (await this.run(() => this.api.update(feature.key, { expected_version: feature.version, update_documents: [patch] }), 'Document saved.')) {
      this.editingDoc.set('');
      await this.reload();
    }
  }

  /** update_work save points (decisions, attempts, corrections, findings) on the feature. */
  async savePoint(fields: Record<string, unknown>): Promise<void> {
    const feature = this.feature()!;
    if (await this.run(() => this.api.update(feature.key, { expected_version: feature.version, ...fields }), 'Save point recorded.')) {
      this.points()?.clear();
      await this.reload();
    }
  }

  /** update_work add_documents. */
  async addDocument(): Promise<void> {
    const feature = this.feature()!;
    if (await this.run(() => this.api.update(feature.key, { expected_version: feature.version, add_documents: [{ title: this.newDocTitle.trim(), body: this.newDocBody }] }), 'Document added.')) {
      this.newDocTitle = this.newDocBody = '';
      await this.reload();
    }
  }

  // ---- sync_plan -------------------------------------------------------------------------------

  fillPlan(): void {
    const feature = this.feature()!;
    const plan = {
      title: feature.title,
      description: feature.description,
      goals: feature.goals,
      requirements: (feature.requirements ?? []).filter(requirement => requirement.state === 'active').map(requirement => ({ id: requirement.id, text: requirement.text })),
      acceptance_tests: feature.acceptance_tests,
      items: this.items().filter(item => item.status !== 'closed').map(item => ({
        ref: item.ref ?? item.key,
        title: item.title,
        type: item.type,
        priority: item.priority,
        goal: item.goal,
        acceptance_criteria: item.acceptance_criteria?.map(criterion => ({ id: criterion.id, text: criterion.text })),
        inherits: item.inherits,
        touches: item.touches,
        blocked_by: item.blocked_by.map(blocker => blocker.key)
      }))
    };
    this.planJson = JSON.stringify(plan, undefined, 2);
    this.previewed.set(null);
  }

  async preview(): Promise<void> {
    this.planError.set('');
    let plan: unknown;
    try {
      plan = JSON.parse(this.planJson);
    } catch (error) {
      this.planError.set(`That is not valid JSON: ${error instanceof Error ? error.message : error}`);
      return;
    }
    const result = await this.run(() => this.api.syncPlan({ feature: this.feature()!.key, plan }));
    if (result && 'preview_token' in result) this.previewed.set(result);
  }

  async confirmPlan(preview: PlanPreview): Promise<void> {
    const result = await this.run(() => this.api.syncPlan({ confirm: preview.preview_token }), 'Plan applied.');
    if (result) {
      this.previewed.set(null);
      await this.reload();
    }
  }

  previewSections(p: PlanPreview): { title: string; rows: string[] }[] {
    const change = (c: { key?: string; ref: string; title: string }) => `${c.key ?? c.ref}: ${c.title}`;
    return [
      { title: 'Requirements added', rows: p.requirements_added ?? [] },
      { title: 'Requirements changed', rows: p.requirements_changed ?? [] },
      { title: 'Requirements dropped', rows: p.requirements_dropped ?? [] },
      { title: 'Items added', rows: (p.items_added ?? []).map(change) },
      { title: 'Items edited', rows: (p.items_edited ?? []).map(c => change(c) + (c.pivot ? ' (in flight: pivot)' : '') + (c.reopens_finished ? ' (reopens a finished item)' : '')) },
      { title: 'Items closed', rows: (p.items_closed ?? []).map(change) },
      { title: 'Dependencies added', rows: (p.dependencies_added ?? []).map(d => `${d.item} blocked by ${d.blocked_by}`) },
      { title: 'Dependencies removed', rows: (p.dependencies_removed ?? []).map(d => `${d.item} no longer blocked by ${d.blocked_by}`) }
    ];
  }
}
