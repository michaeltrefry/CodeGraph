import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ActivityComponent } from './work-activity';
import { ErrorComponent, KeyHolder, WorkContext, WorkPageBase } from './work-base';
import { DialogComponent, FeatureFormComponent, ItemFormComponent, ProjectFormComponent } from './work-forms';
import { KnowledgeComponent } from './work-knowledge-ui';
import { MessageListComponent } from './work-messages-ui';
import { FeatureView, MessageView, ProjectView } from './work.models';

/** Project: goals, member repositories, features with progress, knowledge, recent activity. */
@Component({
  selector: 'app-work-project',
  standalone: true,
  imports: [FormsModule, RouterLink, ErrorComponent, DialogComponent, FeatureFormComponent, ItemFormComponent, ProjectFormComponent, ActivityComponent, KnowledgeComponent, MessageListComponent],
  template: `
    <div class="wrk-page">
      <wrk-error [error]="error()" (reload)="reload()" />
      @if (creating()) {
        <div class="wrk-card">
          <h2>New project</h2>
          <wrk-project-form (saved)="createProject($event)" (cancelled)="back()" />
        </div>
      } @else if (project(); as p) {
        <div class="wrk-head">
          <div>
            <div class="wrk-key">{{ p.key }} · v{{ p.version }}</div>
            <h1>{{ p.name }}</h1>
          </div>
          <span class="wrk-live" [attr.data-state]="feedError() ? 'off' : 'on'">{{ feedError() ? 'reconnecting' : 'live' }}</span>
          <span class="wrk-grow"></span>
          <a class="wrk-btn" routerLink="/work" (click)="ctx.select(p.key)">Now</a>
          <a class="wrk-btn" [routerLink]="['/work/projects', p.key, 'dreaming']">Dreaming</a>
          <a class="wrk-btn" routerLink="/work/messages" (click)="ctx.select(p.key)">Message board</a>
          <button type="button" class="wrk-btn" [disabled]="busy()" (click)="claimNext()">Claim next item</button>
          <button type="button" class="wrk-btn" (click)="dialog.set('item')">New item</button>
          <button type="button" class="wrk-btn" (click)="dialog.set('plan')">Import plan</button>
          <button type="button" class="wrk-btn" (click)="dialog.set('edit')">Edit project</button>
        </div>
        @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }
        <p class="wrk-sub">Created by {{ p.created_by }}. @if (p.description) { {{ p.description }} }</p>

        <div class="wrk-grid-2">
          <div style="display:flex;flex-direction:column;gap:14px">
            <section class="wrk-card" aria-labelledby="wrk-h-features">
              <h2 id="wrk-h-features">Features <span class="wrk-count">{{ features().length }}</span>
                <span class="wrk-grow"></span><button type="button" class="wrk-btn" (click)="dialog.set('feature')">New feature</button></h2>
              @if (features().length === 0) { <div class="wrk-empty">No features yet. Create one, or import a plan.</div> }
              <table class="wrk-table">
                <thead><tr><th>Feature</th><th>Status</th><th style="min-width:160px">Progress</th><th>Items</th></tr></thead>
                <tbody>
                  @for (feature of features(); track feature.id) {
                    <tr>
                      <td><a [routerLink]="['/work/features', feature.key]"><span class="wrk-key">{{ feature.key }}</span> {{ feature.title }}</a></td>
                      <td><span class="wrk-chip" [attr.data-status]="feature.status === 'complete' ? 'complete' : 'in_progress'">{{ feature.status }}</span></td>
                      <td>
                        <div class="wrk-progress" role="img" [attr.aria-label]="percent(feature) + '% finished'">
                          <span class="wrk-p-complete" [style.width.%]="share(feature, 'complete')"></span>
                          <span class="wrk-p-closed" [style.width.%]="share(feature, 'closed')"></span>
                          <span class="wrk-p-flight" [style.width.%]="flight(feature)"></span>
                        </div>
                      </td>
                      <td class="wrk-sub">{{ finished(feature) }} / {{ total(feature) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-know">
              <h2 id="wrk-h-know">Knowledge: decisions, constraints, facts, lessons</h2>
              <wrk-knowledge [project]="p.key" />
            </section>
          </div>

          <div style="display:flex;flex-direction:column;gap:14px">
            <section class="wrk-card" aria-labelledby="wrk-h-goals">
              <h2 id="wrk-h-goals">Goals</h2>
              @if (p.goals.length === 0) { <div class="wrk-empty">No goals recorded.</div> }
              <ul>@for (goal of p.goals; track goal) { <li>{{ goal }}</li> }</ul>
            </section>
            <section class="wrk-card" aria-labelledby="wrk-h-repos">
              <h2 id="wrk-h-repos">Member repositories</h2>
              @if (p.repositories.length === 0) { <div class="wrk-empty">No repositories.</div> }
              <ul class="wrk-list">
                @for (member of p.repositories; track member.repository) {
                  <li><div class="wrk-row"><a [routerLink]="['/repos', member.repository]">{{ member.repository }}</a><span class="wrk-chip">{{ member.role }}</span></div></li>
                }
              </ul>
              @if (counts().length > 0) {
                <div class="wrk-row" style="margin-top:8px">@for (c of counts(); track c.status) { <span class="wrk-chip" [attr.data-status]="c.status">{{ c.status.replace('_', ' ') }} {{ c.count }}</span> }</div>
              }
            </section>
            <section class="wrk-card" aria-labelledby="wrk-h-open">
              <h2 id="wrk-h-open">Open questions <span class="wrk-count">{{ questions().length }}</span></h2>
              <wrk-message-list [messages]="questions()" [project]="p.key" empty="No open questions." (changed)="reload()" />
            </section>
            <section class="wrk-card" aria-labelledby="wrk-h-act">
              <h2 id="wrk-h-act">Recent activity</h2>
              <wrk-activity [entries]="feed()" [limit]="15" />
            </section>
          </div>
        </div>

        @switch (dialog()) {
          @case ('edit') {
            <wrk-dialog heading="Edit project" (closed)="dialog.set('')"><wrk-project-form [initial]="p" (saved)="saveProject($event)" (cancelled)="dialog.set('')" /></wrk-dialog>
          }
          @case ('feature') {
            <wrk-dialog heading="New feature" (closed)="dialog.set('')"><wrk-feature-form (saved)="createFeature($event)" (cancelled)="dialog.set('')" /></wrk-dialog>
          }
          @case ('item') {
            <wrk-dialog heading="New item" (closed)="dialog.set('')"><wrk-item-form (saved)="createItem($event)" (cancelled)="dialog.set('')" /></wrk-dialog>
          }
          @case ('plan') {
            <wrk-dialog heading="Import a plan" (closed)="dialog.set('')">
              <p class="wrk-sub">A plan is all-or-nothing: <code>{{ '{' }}"features": [{{ '{' }}"ref", "title", ...{{ '}' }}], "items": [{{ '{' }}"ref", "title", "feature", "blocked_by": ["ref"], ...{{ '}' }}]{{ '}' }}</code>. At most 200 items and 20 features.</p>
              <textarea class="wrk-input" rows="14" aria-label="Plan JSON" [(ngModel)]="planJson" spellcheck="false"></textarea>
              @if (planError()) { <div class="wrk-error">{{ planError() }}</div> }
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
export class WorkProjectComponent extends WorkPageBase implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly ctx = inject(WorkContext);
  readonly project = signal<ProjectView | null>(null);
  readonly features = signal<FeatureView[]>([]);
  readonly questions = signal<MessageView[]>([]);
  readonly creating = signal(false);
  readonly key = signal('');
  private readonly keys = { project: new KeyHolder('ui-project'), feature: new KeyHolder('ui-feature'), item: new KeyHolder('ui-item'), plan: new KeyHolder('ui-plan') };
  readonly dialog = signal<'' | 'edit' | 'feature' | 'item' | 'plan'>('');
  readonly planError = signal('');
  planJson = '';
  readonly counts = computed(() =>
    Object.entries(this.project()?.item_counts ?? {}).filter(([, count]) => count > 0).map(([status, count]) => ({ status, count })));

  constructor() {
    super();
  }

  ngOnInit(): void {
    this.onKey(this.route, key => this.open(key));
  }

  /** Shows the project of the route (the component is reused when only the key changes) with a clean slate. */
  private open(key: string): void {
    this.unfollow();
    this.key.set(key);
    this.project.set(null);
    this.features.set([]);
    this.questions.set([]);
    this.feed.set([]);
    this.dialog.set('');
    this.error.set(null);
    this.notice.set('');
    this.creating.set(key === 'new');
    if (key === 'new' || !key) return;
    this.follow(key);
    void this.reload();
  }

  ngOnDestroy(): void {
    this.unfollow();
  }

  back(): void {
    void this.router.navigate(['/work']);
  }

  protected async reload(): Promise<void> {
    const key = this.key();
    if (!key || key === 'new') return;
    try {
      const [project, features, questions] = await Promise.all([
        this.api.get<ProjectView>(key),
        this.api.features(key),
        this.api.messages(key, { open: true, limit: 20 }),
        this.loadRecent(key, 20)
      ]).then(([p, f, q]) => [p, f, q] as const);
      if (key !== this.key()) return;
      this.project.set(project);
      this.features.set(features);
      this.questions.set(questions.items);
    } catch (error) {
      await this.run(() => Promise.reject(error));
    }
  }

  total(feature: FeatureView): number {
    return Object.values(feature.item_counts ?? {}).reduce((sum, count) => sum + count, 0);
  }

  finished(feature: FeatureView): number {
    return (feature.item_counts?.['complete'] ?? 0) + (feature.item_counts?.['closed'] ?? 0);
  }

  share(feature: FeatureView, status: string): number {
    const total = this.total(feature);
    return total === 0 ? 0 : ((feature.item_counts?.[status] ?? 0) / total) * 100;
  }

  flight(feature: FeatureView): number {
    const total = this.total(feature);
    return total === 0 ? 0 : (((feature.item_counts?.['in_progress'] ?? 0) + (feature.item_counts?.['in_review'] ?? 0)) / total) * 100;
  }

  percent(feature: FeatureView): number {
    const total = this.total(feature);
    return total === 0 ? 0 : Math.round((this.finished(feature) / total) * 100);
  }

  async createProject(fields: Record<string, unknown>): Promise<void> {
    const created = await this.run(() => this.api.create({ new_project: fields, idempotency_key: this.keys.project.key }));
    this.noteReplay(created);
    if (created?.project) {
      this.keys.project.reset();
      await this.ctx.load();
      this.ctx.select(created.project.key);
      await this.router.navigate(['/work/projects', created.project.key]);
    }
  }

  async saveProject(fields: Record<string, unknown>): Promise<void> {
    const project = this.project()!;
    if (await this.run(() => this.api.update(project.key, { expected_version: project.version, ...fields }), 'Project saved.')) {
      this.dialog.set('');
      await this.reload();
    }
  }

  async createFeature(fields: Record<string, unknown>): Promise<void> {
    const created = await this.run(() => this.api.create({ project: this.project()!.key, feature: fields, idempotency_key: this.keys.feature.key }), 'Feature created.');
    this.noteReplay(created);
    if (created) {
      this.keys.feature.reset();
      this.dialog.set('');
      await this.reload();
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
