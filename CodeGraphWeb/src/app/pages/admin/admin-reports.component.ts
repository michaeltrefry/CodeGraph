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
    <div class="page-header">
      <div>
        <h2>Reports</h2>
        <p class="subtitle">Inspect assistant, MCP, review, and repository-analysis usage.</p>
      </div>
      <button type="button" (click)="load()" [disabled]="loading()">Refresh</button>
    </div>

    <section class="section-card filter-panel">
      <label>
        Report
        <select [(ngModel)]="selectedReport" (ngModelChange)="load()">
          @for (report of reports; track report.key) {
            <option [ngValue]="report.key">{{ report.label }}</option>
          }
        </select>
      </label>
      <label>
        Interval
        <select [(ngModel)]="interval" (ngModelChange)="load()">
          <option value="day">Day</option>
          <option value="week">Week</option>
          <option value="month">Month</option>
        </select>
      </label>
      <label>
        User
        <select [(ngModel)]="user" (ngModelChange)="load()">
          <option value="">All users</option>
          @for (value of filters()?.users ?? []; track value) {
            <option [value]="value">{{ value }}</option>
          }
        </select>
      </label>
      <label>
        Provider
        <select [(ngModel)]="provider" (ngModelChange)="load()">
          <option value="">All providers</option>
          @for (value of filters()?.providers ?? []; track value) {
            <option [value]="value">{{ value }}</option>
          }
        </select>
      </label>
    </section>

    @if (loading()) {
      <div class="section-card muted">Loading report...</div>
    } @else if (error()) {
      <div class="banner error">{{ error() }}</div>
    } @else if (report(); as current) {
      <section class="summary-grid">
        @for (total of current.totals; track total.key) {
          <article class="summary-card">
            <span>{{ total.label }}</span>
            <strong>{{ total.value }}</strong>
          </article>
        }
      </section>

      <section class="section-card">
        <div class="section-header">
          <h3>Series</h3>
          <span class="muted">{{ current.range.start | date:'mediumDate' }} - {{ current.range.end | date:'mediumDate' }}</span>
        </div>
        @if (current.series.length === 0) {
          <p class="empty">No series data for this range.</p>
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

      <section class="section-card">
        <h3>Breakdowns</h3>
        @if (current.breakdowns.length === 0) {
          <p class="empty">No breakdowns available.</p>
        } @else {
          <table class="data-table">
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
                  <td>{{ item.value }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </section>
    }
  `,
  styles: [`
    :host { display: block; }
    .page-header, .section-header, .filter-panel {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
      justify-content: space-between;
    }
    .page-header { margin-bottom: 1rem; }
    h2, h3 { margin: 0; color: #111827; }
    .subtitle, .muted, .empty { color: #6b7280; margin: 0.25rem 0 0; }
    .filter-panel { flex-wrap: wrap; justify-content: flex-start; }
    label { color: #374151; display: flex; flex-direction: column; font-weight: 600; gap: 0.25rem; }
    select {
      min-width: 180px;
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
      min-height: 36px;
      padding: 0.45rem 0.6rem;
    }
    button {
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: white;
      color: #374151;
      cursor: pointer;
      min-height: 36px;
      padding: 0.45rem 0.8rem;
    }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .section-card, .summary-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 1rem;
      margin-bottom: 1rem;
    }
    .summary-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
      gap: 0.75rem;
      margin-bottom: 1rem;
    }
    .summary-card { margin: 0; }
    .summary-card span { color: #6b7280; display: block; font-weight: 600; }
    .summary-card strong { color: #111827; display: block; font-size: 1.8rem; margin-top: 0.25rem; }
    .series-list { display: flex; flex-direction: column; gap: 0.85rem; margin-top: 0.75rem; }
    .series-row { display: grid; grid-template-columns: minmax(120px, 180px) 1fr; gap: 0.75rem; align-items: end; }
    .series-label { color: #374151; font-weight: 600; }
    .series-bars {
      align-items: end;
      border-bottom: 1px solid #d1d5db;
      display: flex;
      gap: 4px;
      height: 120px;
      min-width: 0;
    }
    .bar {
      background: #2563eb;
      border-radius: 3px 3px 0 0;
      display: inline-block;
      min-height: 2px;
      width: 18px;
    }
    .data-table { width: 100%; border-collapse: collapse; margin-top: 0.75rem; }
    .data-table th, .data-table td {
      border-bottom: 1px solid #e5e7eb;
      padding: 0.6rem;
      text-align: left;
    }
    .banner { border-radius: 8px; padding: 0.7rem 0.85rem; }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
    @media (max-width: 720px) {
      .series-row { grid-template-columns: 1fr; }
      .data-table { display: block; overflow-x: auto; }
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
