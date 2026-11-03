import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { RepositoryReviewResponse } from '../../core/models';
import { ErrorComponent, KeyHolder, WorkContext, WorkPageBase } from '../work/work-base';
import { DialogComponent, ProjectFormComponent } from '../work/work-forms';
import { AgoPipe } from '../work/work-shared';
import { ProjectSignalsService, projectRow, toReviewCount } from './project-data';

/** Projects: every work project with its repositories, progress, work in flight and what waits for review. */
@Component({
  selector: 'app-projects-list',
  standalone: true,
  imports: [RouterLink, AgoPipe, ErrorComponent, DialogComponent, ProjectFormComponent],
  styleUrl: './projects.scss',
  template: `
    <div class="cg-page">
      <header class="cg-page-header">
        <div>
          <h1>Projects</h1>
          <p>Every top-level project, with its repositories, work in flight and what is waiting for review.</p>
        </div>
        <div class="cg-page-header-actions">
          <button type="button" class="cg-btn primary" (click)="creating.set(true)">New project</button>
        </div>
      </header>

      @if (ctx.error()) { <div class="wrk-error" role="alert">{{ ctx.error() }}</div> }

      <div class="cg-card">
        <div class="prj-scroll">
          <table class="cg-table prj-table">
            <thead>
              <tr>
                <th>Project</th>
                <th>Repositories</th>
                <th class="prj-progress-cell">Progress</th>
                <th class="prj-num">In progress</th>
                <th class="prj-num">To review</th>
                <th class="prj-num">Updated</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.key) {
                <tr (click)="open(row.key)">
                  <td>
                    <div class="prj-name-line">
                      <span class="wrk-key">{{ row.key }}</span>
                      <a class="prj-name" [routerLink]="['/projects', row.key, 'overview']" (click)="$event.stopPropagation()">{{ row.name }}</a>
                    </div>
                    @if (row.description) { <div class="prj-desc">{{ row.description }}</div> }
                  </td>
                  <td><span class="prj-repos">{{ row.repositories }}</span></td>
                  <td class="prj-progress-cell">
                    <div class="prj-progress">
                      <div class="wrk-progress" role="img" [attr.aria-label]="row.pct + '% finished'">
                        <span class="wrk-p-complete" [style.width.%]="row.done"></span>
                        <span class="wrk-p-flight" [style.width.%]="row.flight"></span>
                      </div>
                      <span class="prj-pct">{{ row.pct }}%</span>
                    </div>
                  </td>
                  <td class="cg-cell-num">{{ row.inFlight }}</td>
                  <td class="cg-cell-num" [attr.title]="row.toReview === undefined ? 'Counting…' : row.toReview === null ? 'Could not be counted' : 'Open approvals and code reviews that failed or are stale'">{{ row.toReview === undefined ? '…' : row.toReview ?? '—' }}</td>
                  <td class="prj-num"><span class="prj-updated" [attr.title]="row.updatedAt">{{ row.updatedAt | ago }}</span></td>
                </tr>
              } @empty {
                <tr><td colspan="6"><div class="wrk-empty">{{ ctx.loaded() ? 'There are no projects yet. Create one, or have an agent call create_work.' : 'Loading…' }}</div></td></tr>
              }
            </tbody>
          </table>
        </div>
      </div>

      @if (creating()) {
        <wrk-dialog heading="New project" (closed)="creating.set(false)">
          <wrk-error [error]="error()" [reloadable]="false" />
          <wrk-project-form (saved)="createProject($event)" (cancelled)="creating.set(false)" />
        </wrk-dialog>
      }
    </div>
  `
})
export class ProjectsListComponent extends WorkPageBase implements OnInit {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly signals = inject(ProjectSignalsService);
  readonly ctx = inject(WorkContext);
  readonly creating = signal(false);
  /** To-review count per project key: missing while it is being counted, null when it could not be. */
  readonly toReview = signal<Record<string, number | null>>({});
  readonly rows = computed(() => this.ctx.projects().map(project => projectRow(project, this.toReview()[project.key])));
  private readonly createKey = new KeyHolder('ui-project');
  private generation = 0;

  constructor() {
    super();
  }

  ngOnInit(): void {
    if (this.route.snapshot.queryParamMap.get('new')) this.creating.set(true);
    void this.reload();
  }

  protected async reload(): Promise<void> {
    const generation = ++this.generation;
    await this.ctx.load();
    if (generation !== this.generation) return;
    const reviews = new Map<string, Promise<RepositoryReviewResponse | null>>();
    await Promise.all(this.ctx.projects().map(async project => {
      try {
        const [approvals, latest] = await Promise.all([
          this.signals.openApprovals(project.key),
          this.signals.latestReviews(project.repositories.map(member => member.repository), reviews)
        ]);
        if (generation === this.generation) this.toReview.update(counts => ({ ...counts, [project.key]: toReviewCount(approvals, latest) }));
      } catch {
        if (generation === this.generation) this.toReview.update(counts => ({ ...counts, [project.key]: null }));
      }
    }));
  }

  open(key: string): void {
    void this.router.navigate(['/projects', key, 'overview']);
  }

  async createProject(fields: Record<string, unknown>): Promise<void> {
    const created = await this.run(() => this.api.create({ new_project: fields, idempotency_key: this.createKey.key }));
    this.noteReplay(created);
    if (created?.project) {
      this.createKey.reset();
      this.creating.set(false);
      await this.ctx.load();
      this.ctx.select(created.project.key);
      await this.router.navigate(['/projects', created.project.key, 'overview']);
    }
  }
}
