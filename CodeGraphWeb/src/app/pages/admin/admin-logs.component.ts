import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ApplicationLogEntryResponse, ApplicationLogPageResponse } from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

@Component({
  selector: 'app-admin-logs',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Application logs</h1>
        <p>Inspect API and browser errors without relying on the external observability stack.</p>
      </div>
      <button class="adm-btn" type="button" (click)="load()" [disabled]="loading()">
        {{ loading() ? 'Refreshing…' : 'Refresh' }}
      </button>
    </header>

    <form class="adm-card filter-panel" (ngSubmit)="applyFilters()">
      <label class="adm-field level-field">
        <span class="adm-field-label">Level</span>
        <select class="adm-select" name="level" [(ngModel)]="level" [disabled]="loading()">
          <option value="">All levels</option>
          @for (option of levels; track option) {
            <option [value]="option">{{ option }}</option>
          }
        </select>
      </label>

      <label class="adm-field time-field">
        <span class="adm-field-label">From</span>
        <input class="adm-input" type="datetime-local" name="start" [(ngModel)]="start" [disabled]="loading()">
      </label>

      <label class="adm-field time-field">
        <span class="adm-field-label">To</span>
        <input class="adm-input" type="datetime-local" name="end" [(ngModel)]="end" [disabled]="loading()">
      </label>

      <label class="adm-field search-field">
        <span class="adm-field-label">Search</span>
        <input
          class="adm-input"
          type="search"
          name="search"
          [(ngModel)]="search"
          maxlength="256"
          placeholder="Message, category, exception, or source"
          [disabled]="loading()">
      </label>

      <div class="filter-actions">
        <button class="adm-btn primary" type="submit" [disabled]="loading()">{{ loading() ? 'Loading…' : 'Apply' }}</button>
        <button
          class="adm-btn"
          type="button"
          (click)="clearFilters()"
          [disabled]="loading() || !hasFilters()"
          [title]="!hasFilters() ? 'No filters to clear' : 'Clear all filters'">Clear</button>
      </div>
    </form>

    @if (error()) {
      <div class="adm-banner err" role="alert">{{ error() }}</div>
    }

    <section class="adm-card adm-card-flush log-card" aria-labelledby="log-results-heading">
      <header class="adm-card-head results-head">
        <div>
          <span class="adm-section-label" id="log-results-heading">Log entries</span>
          <span class="result-count" aria-live="polite">{{ resultSummary() }}</span>
        </div>
        <span class="page-size">100 per page</span>
      </header>

      @if (loading() && !result()) {
        <div class="state-message" aria-live="polite">Loading application logs…</div>
      } @else if (!loading() && result()?.entries?.length === 0) {
        <div class="state-message">No log entries match these filters.</div>
      } @else if (result(); as current) {
        <div class="table-scroll" [class.is-refreshing]="loading()">
          <table class="cg-table log-table">
            <caption>Application logs ordered newest first</caption>
            <thead>
              <tr>
                <th scope="col">Time (local)</th>
                <th scope="col">Level</th>
                <th scope="col">Source</th>
                <th scope="col">Message</th>
              </tr>
            </thead>
            <tbody>
              @for (entry of current.entries; track entry.id) {
                <tr>
                  <td class="time-cell">
                    <time [attr.datetime]="entry.occurredAtUtc" [title]="entry.occurredAtUtc">
                      {{ entry.occurredAtUtc | date:'yyyy-MM-dd HH:mm:ss.SSS' }}
                    </time>
                  </td>
                  <td>
                    <span class="level-badge" [class]="levelClass(entry.level)">{{ entry.level }}</span>
                  </td>
                  <td class="source-cell">
                    <strong>{{ entry.source }}</strong>
                    <span>{{ entry.category }}</span>
                  </td>
                  <td class="message-cell">
                    <div class="message-text">{{ entry.message }}</div>
                    @if (hasDetails(entry)) {
                      <details class="log-details">
                        <summary>Details</summary>
                        <dl>
                          @if (entry.eventId) {
                            <div><dt>Event</dt><dd>{{ entry.eventId }}</dd></div>
                          }
                          @if (entry.traceId) {
                            <div><dt>Trace</dt><dd>{{ entry.traceId }}</dd></div>
                          }
                          @if (entry.spanId) {
                            <div><dt>Span</dt><dd>{{ entry.spanId }}</dd></div>
                          }
                        </dl>
                        @if (entry.exception) {
                          <h2>Exception</h2>
                          <pre>{{ entry.exception }}</pre>
                        }
                        @if (entry.propertiesJson) {
                          <h2>Properties</h2>
                          <pre>{{ formatProperties(entry.propertiesJson) }}</pre>
                        }
                      </details>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      <nav class="pager" aria-label="Log pages">
        <button class="adm-btn" type="button" (click)="previousPage()" [disabled]="loading() || page <= 1">Previous</button>
        <span aria-live="polite">Page {{ page }}{{ totalPages() > 0 ? ' of ' + totalPages() : '' }}</span>
        <button class="adm-btn" type="button" (click)="nextPage()" [disabled]="loading() || page >= totalPages()">Next</button>
      </nav>
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }

    .filter-panel {
      display: grid;
      grid-template-columns: minmax(130px, .55fr) minmax(190px, .8fr) minmax(190px, .8fr) minmax(260px, 1.5fr) auto;
      align-items: end;
      gap: 12px;
    }

    .filter-actions { display: flex; gap: 8px; }
    .filter-actions .adm-btn, .adm-page-header .adm-btn, .pager .adm-btn { min-height: 44px; }
    .adm-input, .adm-select { min-height: 44px; }
    .adm-input:focus-visible, .adm-select:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    .adm-btn:active:not(:disabled) { transform: scale(.98); }

    .results-head > div { display: flex; align-items: baseline; gap: 10px; }
    .result-count, .page-size, .pager { color: var(--muted); font-size: var(--fs-sm); }
    .page-size { font-family: var(--font-mono); }

    .table-scroll { overflow-x: auto; transition: opacity var(--transition); }
    .table-scroll.is-refreshing { opacity: .55; }
    .log-table { min-width: 940px; table-layout: fixed; }
    .log-table caption {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
    .log-table th:nth-child(1) { width: 190px; }
    .log-table th:nth-child(2) { width: 110px; }
    .log-table th:nth-child(3) { width: 260px; }
    .log-table td { vertical-align: top; }

    .time-cell {
      color: var(--text-2);
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }

    .level-badge {
      display: inline-flex;
      align-items: center;
      min-height: 24px;
      padding: 2px 8px;
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--surface-2);
      color: var(--text-2);
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      font-weight: 600;
    }
    .level-warning { color: var(--sem-amber); background: var(--warn-bg); border-color: color-mix(in oklab, var(--sem-amber) 35%, var(--border)); }
    .level-error, .level-critical { color: var(--sem-red); background: var(--err-bg); border-color: color-mix(in oklab, var(--sem-red) 35%, var(--border)); }
    .level-information { color: var(--sem-blue); background: color-mix(in oklab, var(--sem-blue) 11%, transparent); border-color: color-mix(in oklab, var(--sem-blue) 35%, var(--border)); }

    .source-cell { overflow-wrap: anywhere; }
    .source-cell strong, .source-cell span { display: block; }
    .source-cell strong { color: var(--text); font-size: var(--fs-sm); font-weight: 600; }
    .source-cell span { margin-top: 3px; color: var(--muted); font-family: var(--font-mono); font-size: var(--fs-xs); line-height: 1.45; }
    .message-text { color: var(--text); line-height: 1.5; overflow-wrap: anywhere; white-space: pre-wrap; }

    .log-details { margin-top: 7px; }
    .log-details summary {
      width: fit-content;
      min-height: 44px;
      padding: 12px 2px;
      color: var(--accent-ink);
      cursor: pointer;
      font-size: var(--fs-sm);
      font-weight: 600;
    }
    .log-details summary:hover { color: var(--accent); }
    .log-details summary:focus-visible, .adm-btn:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    .log-details dl { display: flex; flex-wrap: wrap; gap: 8px 18px; margin: 2px 0 10px; }
    .log-details dl div { min-width: 0; }
    .log-details dt { color: var(--muted); font-size: var(--fs-xs); text-transform: uppercase; letter-spacing: .05em; }
    .log-details dd { margin: 2px 0 0; color: var(--text-2); font-family: var(--font-mono); font-size: var(--fs-xs); overflow-wrap: anywhere; }
    .log-details h2 { margin: 12px 0 6px; color: var(--muted); font-size: var(--fs-xs); text-transform: uppercase; letter-spacing: .05em; }
    .log-details pre {
      max-height: 300px;
      margin: 0;
      padding: 10px 12px;
      overflow: auto;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-3);
      color: var(--text-2);
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      line-height: 1.5;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .state-message { padding: 52px 20px; color: var(--muted); text-align: center; }
    .pager {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 14px;
      padding: 14px 20px;
      border-top: 1px solid var(--hairline);
    }
    .pager span { min-width: 110px; text-align: center; font-family: var(--font-mono); }

    @media (max-width: 1080px) {
      .filter-panel { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .search-field { grid-column: 1 / -1; }
      .filter-actions { grid-column: 1 / -1; }
    }

    @media (max-width: 620px) {
      .filter-panel { grid-template-columns: 1fr; }
      .search-field, .filter-actions { grid-column: auto; }
      .filter-actions .adm-btn { flex: 1; justify-content: center; }
      .results-head { align-items: flex-start; }
      .results-head > div { align-items: flex-start; flex-direction: column; gap: 4px; }
    }

    @media (prefers-reduced-motion: reduce) {
      .table-scroll { transition: none; }
      .adm-btn:active:not(:disabled) { transform: none; }
    }
  `]
})
export class AdminLogsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private loadSequence = 0;

  readonly levels = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'];
  readonly result = signal<ApplicationLogPageResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');

  level = '';
  start = '';
  end = '';
  search = '';
  page = 1;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    const sequence = ++this.loadSequence;
    this.loading.set(true);
    this.error.set('');

    try {
      const response = await firstValueFrom(this.api.getApplicationLogs(this.currentFilters()));
      if (sequence === this.loadSequence) this.result.set(response);
    } catch (err) {
      if (sequence === this.loadSequence) {
        this.error.set(extractAdminError(err, 'Failed to load application logs'));
      }
    } finally {
      if (sequence === this.loadSequence) this.loading.set(false);
    }
  }

  async applyFilters(): Promise<void> {
    this.page = 1;
    await this.load();
  }

  async clearFilters(): Promise<void> {
    this.level = '';
    this.start = '';
    this.end = '';
    this.search = '';
    this.page = 1;
    await this.load();
  }

  async previousPage(): Promise<void> {
    if (this.page <= 1) return;
    this.page -= 1;
    await this.load();
  }

  async nextPage(): Promise<void> {
    if (this.page >= this.totalPages()) return;
    this.page += 1;
    await this.load();
  }

  totalPages(): number {
    return this.result()?.totalPages ?? 0;
  }

  resultSummary(): string {
    const current = this.result();
    if (!current) return this.loading() ? 'Loading' : '';
    const noun = current.totalCount === 1 ? 'entry' : 'entries';
    return `${current.totalCount.toLocaleString()} ${noun}`;
  }

  hasFilters(): boolean {
    return Boolean(this.level || this.start || this.end || this.search.trim());
  }

  hasDetails(entry: ApplicationLogEntryResponse): boolean {
    return Boolean(entry.exception || entry.propertiesJson || entry.traceId || entry.spanId || entry.eventId);
  }

  levelClass(level: string): string {
    return `level-badge level-${level.toLowerCase()}`;
  }

  formatProperties(value: string): string {
    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  private currentFilters(): { page: number; level?: string; start?: string; end?: string; search?: string } {
    return {
      page: this.page,
      level: this.level || undefined,
      start: this.toUtc(this.start),
      end: this.toUtc(this.end),
      search: this.search.trim() || undefined
    };
  }

  private toUtc(value: string): string | undefined {
    if (!value) return undefined;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
  }
}
