import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AdminReportFiltersResponse, AdminReportResponse } from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

type ReportKey = 'assistant/usage' | 'assistant/activity' | 'mcp/usage' | 'code-review/usage' | 'repository-analysis/usage';

@Component({
  selector: 'app-admin-reports',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Reports</h1>
        <p>Inspect assistant, MCP, review, and repository-analysis usage.</p>
      </div>
      <button class="adm-btn" type="button" (click)="load()" [disabled]="loading()">Refresh</button>
    </header>

    <section class="adm-card filter-panel">
      <label class="adm-field">
        <span class="adm-field-label">Report</span>
        <select class="adm-select" [(ngModel)]="selectedReport" (ngModelChange)="load()">
          @for (report of reports; track report.key) {
            <option [ngValue]="report.key">{{ report.label }}</option>
          }
        </select>
      </label>
      <label class="adm-field narrow">
        <span class="adm-field-label">Interval</span>
        <select class="adm-select" [(ngModel)]="interval" (ngModelChange)="load()">
          <option value="day">Day</option>
          <option value="week">Week</option>
          <option value="month">Month</option>
        </select>
      </label>
      <label class="adm-field">
        <span class="adm-field-label">User</span>
        <select class="adm-select" [(ngModel)]="user" (ngModelChange)="load()">
          <option value="">All users</option>
          @for (value of filters()?.users ?? []; track value) {
            <option [value]="value">{{ value }}</option>
          }
        </select>
      </label>
      <label class="adm-field">
        <span class="adm-field-label">Provider</span>
        <select class="adm-select" [(ngModel)]="provider" (ngModelChange)="load()">
          <option value="">All providers</option>
          @for (value of filters()?.providers ?? []; track value) {
            <option [value]="value">{{ value }}</option>
          }
        </select>
      </label>
    </section>

    @if (loading()) {
      <div class="adm-card cg-muted">Loading report...</div>
    } @else if (error()) {
      <div class="adm-banner err">{{ error() }}</div>
    } @else if (report(); as current) {
      <section class="summary-grid">
        @for (total of current.totals; track total.key) {
          <article class="adm-card summary-card">
            <span>{{ total.label }}</span>
            <strong>{{ total.value }}</strong>
          </article>
        }
      </section>

      <section class="adm-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Series</span>
          <span class="cg-muted cg-small">{{ current.range.start | date:'mediumDate' }} - {{ current.range.end | date:'mediumDate' }}</span>
        </header>
        @if (current.series.length === 0) {
          <p class="empty cg-muted">No series data for this range.</p>
        } @else {
          <div class="series-list">
            @for (series of current.series; track series.key) {
              <div class="series-row">
                <div class="series-label">{{ series.label }}</div>
                <div class="series-bars">
                  @for (point of series.points; track point.bucketStart) {
                    <span
                      class="bar"
                      [style.height.%]="barHeight(point.value, series.points)"
                      [title]="(point.bucketStart | date:'mediumDate') + ': ' + point.value">
                    </span>
                  }
                </div>
              </div>
            }
          </div>
        }
      </section>

      <section class="adm-card adm-card-flush">
        <header class="adm-card-head">
          <span class="adm-section-label">Breakdowns</span>
        </header>
        @if (current.breakdowns.length === 0) {
          <p class="empty cg-muted">No breakdowns available.</p>
        } @else {
          <table class="cg-table">
            <thead>
              <tr>
                <th>Dimension</th>
                <th>Label</th>
                <th>Value</th>
              </tr>
            </thead>
            <tbody>
              @for (item of current.breakdowns; track item.dimension + item.key) {
                <tr>
                  <td>{{ item.dimension }}</td>
                  <td>{{ item.label }}</td>
                  <td class="cg-cell-num">{{ item.value }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </section>
    }
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
    .filter-panel {
      display: grid;
      grid-template-columns: minmax(220px, 1.4fr) minmax(140px, 0.7fr) minmax(180px, 1fr) minmax(180px, 1fr);
      gap: 12px;
    }
    .filter-panel .adm-field { flex: 0 1 auto; }
    .summary-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
      gap: 0.75rem;
    }
    .summary-card span { color: var(--muted); display: block; font-weight: 600; }
    .summary-card strong { color: var(--text); display: block; font-size: 1.8rem; margin-top: 0.25rem; }
    .series-list { display: flex; flex-direction: column; gap: 0.85rem; margin-top: 0.75rem; }
    .series-row { display: grid; grid-template-columns: minmax(120px, 180px) 1fr; gap: 0.75rem; align-items: end; }
    .series-label { color: var(--text-2); font-weight: 600; }
    .series-bars {
      align-items: end;
      border-bottom: 1px solid var(--border);
      display: flex;
      gap: 4px;
      height: 120px;
      min-width: 0;
    }
    .bar {
      background: var(--accent);
      border-radius: 3px 3px 0 0;
      display: inline-block;
      min-height: 2px;
      width: 18px;
    }
    .empty { margin: 0; padding: 16px 20px; }
    @media (max-width: 720px) {
      .filter-panel { grid-template-columns: 1fr; }
      .series-row { grid-template-columns: 1fr; }
      .cg-table { display: block; overflow-x: auto; }
    }
  `]
})
export class AdminReportsComponent implements OnInit {
  private api = inject(ApiService);

  reports: { key: ReportKey; label: string }[] = [
    { key: 'assistant/usage', label: 'Assistant Usage' },
    { key: 'assistant/activity', label: 'Assistant Activity' },
    { key: 'mcp/usage', label: 'MCP Usage' },
    { key: 'code-review/usage', label: 'Code Review Usage' },
    { key: 'repository-analysis/usage', label: 'Repository Analysis Usage' }
  ];

  selectedReport: ReportKey = 'assistant/usage';
  interval = 'day';
  user = '';
  provider = '';

  report = signal<AdminReportResponse | null>(null);
  filters = signal<AdminReportFiltersResponse | null>(null);
  loading = signal(false);
  error = signal('');

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const filter = this.currentFilter();
      const [report, filters] = await Promise.all([
        firstValueFrom(this.api.getAdminReport(this.selectedReport, filter)),
        firstValueFrom(this.api.getAdminReportFilters(filter))
      ]);
      this.report.set(report);
      this.filters.set(filters);
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to load report'));
    } finally {
      this.loading.set(false);
    }
  }

  barHeight(value: number, points: { value: number }[]): number {
    const max = Math.max(...points.map(point => point.value), 1);
    return Math.max(4, Math.round((value / max) * 100));
  }

  private currentFilter(): { interval: string; user?: string; provider?: string } {
    return {
      interval: this.interval,
      user: this.user || undefined,
      provider: this.provider || undefined
    };
  }
}
