import { DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom, Observable } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { JobQueueEntry, JobQueueResponse } from '../../core/models';

const operationLabels: Record<string, string> = {
  process_repository: 'Index repository', process_repositories: 'Process repositories',
  reindex_all: 'Re-index all', discover: 'Discover repositories', link: 'Link repositories',
  detect_communities: 'Detect communities', link_and_detect: 'Link and detect communities',
  process_batch_analysis: 'Process batch analysis', sync_schema: 'Sync schema',
  sync_all_schemas: 'Sync all schemas', branch_overlay: 'Branch overlay', reanalyze: 'Re-analyze repository',
  'analysis-batch': 'Analysis batch', Discover: 'Discover repositories', ReIndexAll: 'Re-index all',
  ProcessBatchAnalysis: 'Process batch analysis', LinkAndDetect: 'Link and detect communities',
  DetectCommunities: 'Detect communities', RegenerateMcpDocs: 'Regenerate MCP docs',
  AssistantRetentionCleanup: 'Assistant retention cleanup', IngestConventionEmbeddings: 'Ingest convention embeddings',
  PruneWorkSessions: 'Prune work sessions', PruneWorkMessages: 'Prune work messages',
  WorkImport: 'Import work', IndexerRunRetention: 'Indexer run retention'
};

export function parseUtcDate(value: string | null): Date | null {
  if (!value) return null;
  const date = new Date(/(?:z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

@Component({
  selector: 'app-admin-job-queue',
  standalone: true,
  imports: [DatePipe],
  template: `
    <section class="adm-card adm-card-flush" aria-labelledby="job-queue-heading">
      <header class="adm-card-head queue-header">
        <h2 id="job-queue-heading" class="adm-section-label">Job queue</h2>
        <div class="counts" aria-label="Job counts">
          <span class="cg-chip cg-chip-accent">{{ queue()?.running ?? 0 }} running</span>
          <span class="cg-chip cg-chip-warn">{{ queue()?.queued ?? 0 }} queued</span>
          <span class="cg-chip cg-chip-err">{{ queue()?.failed ?? 0 }} failed</span>
        </div>
      </header>
      @if (error()) { <div class="adm-banner err" role="alert">{{ error() }}</div> }
      @if (loading() && !queue()) { <p class="queue-state" role="status">Loading job queue…</p> }
      @if (queue(); as data) {
        <div class="table-scroll">
          <table class="cg-table" aria-label="Job queue">
            <thead><tr><th>Job</th><th>Target</th><th>Status</th><th>Requested by</th><th>Since</th><th>Detail</th><th>Actions</th></tr></thead>
            <tbody>
              @for (item of data.items; track identity(item)) {
                <tr>
                  <td>{{ operationLabel(item.operation) }}</td>
                  <td>{{ item.target || '—' }}</td>
                  <td><span class="cg-chip cg-chip-dot" [class.cg-chip-accent]="item.status === 'running'" [class.cg-chip-err]="item.status === 'failed'" [class.cg-chip-warn]="item.status === 'queued'">{{ item.status }}</span></td>
                  <td>{{ item.requestedByUsername || '—' }}</td>
                  <td class="since">{{ parseUtcDate(item.status === 'running' ? (item.startedAt || item.createdAt) : item.createdAt) | date:'medium' }}</td>
                  <td class="detail" [class.failure]="item.status === 'failed'">
                    @if (item.status === 'failed') { {{ item.error || item.message || 'Failed' }} }
                    @else if (item.status === 'queued' && item.nextAttemptAt) { Retrying at {{ parseUtcDate(item.nextAttemptAt) | date:'medium' }} }
                    @else { {{ item.message || '—' }} }
                  </td>
                  <td>
                    @if (item.status === 'failed') {
                      <button class="adm-btn sm" type="button" (click)="dismiss(item)" [disabled]="pending().has(identity(item))">Dismiss</button>
                    } @else if (item.cancelable) {
                      <button class="adm-btn ghost-danger sm" type="button" (click)="cancel(item)" [disabled]="pending().has(identity(item))">Cancel</button>
                    }
                  </td>
                </tr>
              }
              @if (!data.items.length) { <tr><td colspan="7" class="queue-state">Nothing is running, queued or failed.</td></tr> }
            </tbody>
          </table>
        </div>
        @if (data.items.length < total()) { <p class="queue-state">Showing {{ data.items.length }} of {{ total() }}</p> }
      }
    </section>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .queue-header, .counts { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .queue-header { justify-content: space-between; }
    h2 { margin: 0; }
    .table-scroll { overflow-x: auto; }
    .since { min-width: 150px; }
    .detail { min-width: 200px; max-width: 480px; white-space: pre-wrap; overflow-wrap: anywhere; }
    .failure { color: var(--sem-red); font-family: var(--font-mono); }
    .queue-state { padding: 16px; color: var(--muted); text-align: center; margin: 0; }
  `]
})
export class AdminJobQueueComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  private inFlight: Promise<void> | null = null;
  readonly queue = signal<JobQueueResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly pending = signal(new Set<string>());
  readonly total = computed(() => {
    const data = this.queue();
    return data ? data.running + data.queued + data.failed : 0;
  });
  readonly parseUtcDate = parseUtcDate;

  constructor() {
    const timer = setInterval(() => void this.refresh(), 5000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
    void this.refresh();
  }

  identity(item: JobQueueEntry): string { return `${item.kind}:${item.id}:${item.status}`; }
  operationLabel(operation: string): string { return operationLabels[operation] ?? operation; }

  refresh(): Promise<void> {
    if (this.destroyRef.destroyed) return Promise.resolve();
    if (this.inFlight) return this.inFlight;
    this.inFlight = this.load().finally(() => { this.inFlight = null; });
    return this.inFlight;
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      const data = await firstValueFrom(this.api.getJobQueue().pipe(takeUntilDestroyed(this.destroyRef)));
      if (!this.destroyRef.destroyed) {
        this.queue.set(data);
        this.error.set('');
      }
    } catch (err: unknown) {
      if (!this.destroyRef.destroyed) this.error.set(this.errorMessage(err, 'Failed to load job queue.'));
    } finally {
      if (!this.destroyRef.destroyed) this.loading.set(false);
    }
  }

  async dismiss(item: JobQueueEntry): Promise<void> { await this.act(item, false); }
  async cancel(item: JobQueueEntry): Promise<void> { await this.act(item, true); }

  private async act(item: JobQueueEntry, cancel: boolean): Promise<void> {
    const key = this.identity(item);
    if (this.pending().has(key)) return;
    this.pending.update(value => new Set(value).add(key));
    this.error.set('');
    try {
      const request: Observable<unknown> = cancel ? this.api.cancelIndexerRun(item.id) : this.api.dismissJob(item.kind, item.id);
      await firstValueFrom(request.pipe(takeUntilDestroyed(this.destroyRef)));
      // Finish any pre-action poll before requesting the authoritative post-action snapshot.
      if (this.inFlight) await this.inFlight;
      await this.refresh();
    } catch (err: unknown) {
      if (!this.destroyRef.destroyed) this.error.set(this.errorMessage(err, `Failed to ${cancel ? 'cancel' : 'dismiss'} job.`));
    } finally {
      if (!this.destroyRef.destroyed) this.pending.update(value => { const next = new Set(value); next.delete(key); return next; });
    }
  }

  private errorMessage(error: unknown, fallback: string): string {
    const err = error as { error?: { message?: string } | string; message?: string };
    return (typeof err?.error === 'string' ? err.error : err?.error?.message) || err?.message || fallback;
  }
}
