import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { DatabaseHealthResponse } from '../../core/models';

@Component({
  selector: 'app-admin-db-health',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="page-header">
      <div>
        <h2>DB Health</h2>
        <p class="subtitle">Check for schema drift, offline indexes, and duplicate hot spots that can hurt insert performance.</p>
      </div>
      <button class="primary" (click)="load()" [disabled]="loading()">
        {{ loading() ? 'Refreshing…' : 'Refresh' }}
      </button>
    </div>

    @if (error()) {
      <div class="banner error">{{ error() }}</div>
    }

    @if (health(); as h) {
      <div class="summary-grid">
        <div class="summary-card">
          <div class="summary-label">Overall</div>
          <div class="summary-value">
            <span class="status-pill" [class.healthy]="h.status === 'healthy'" [class.warning]="h.status === 'warning'" [class.critical]="h.status === 'critical'">
              {{ h.status }}
            </span>
          </div>
          <div class="summary-meta">Captured {{ h.capturedAt | date:'medium' }}</div>
        </div>

        <div class="summary-card">
          <div class="summary-label">Constraints</div>
          <div class="summary-value">{{ h.constraintCount }}/{{ h.expectedConstraintCount }}</div>
          <div class="summary-meta">{{ h.missingConstraints.length }} missing</div>
        </div>

        <div class="summary-card">
          <div class="summary-label">Indexes</div>
          <div class="summary-value">{{ h.indexCount }}/{{ h.expectedIndexCount }}</div>
          <div class="summary-meta">{{ h.missingIndexes.length }} missing, {{ h.offlineIndexes.length }} offline</div>
        </div>

        <div class="summary-card">
          <div class="summary-label">Duplicate Hot Spots</div>
          <div class="summary-value">{{ h.duplicateGroups.length }}</div>
          <div class="summary-meta">Top 50 issue groups</div>
        </div>
      </div>

      <div class="section-card">
        <div class="section-header">
          <h3>Missing Constraints</h3>
          <span class="section-state" [class.ok]="h.missingConstraints.length === 0">{{ h.missingConstraints.length === 0 ? 'Healthy' : h.missingConstraints.length + ' missing' }}</span>
        </div>
        @if (h.missingConstraints.length === 0) {
          <p class="empty">All expected named constraints are present.</p>
        } @else {
          <div class="chip-list">
            @for (name of h.missingConstraints; track name) {
              <code>{{ name }}</code>
            }
          </div>
        }
      </div>

      <div class="section-card">
        <div class="section-header">
          <h3>Missing Indexes</h3>
          <span class="section-state" [class.ok]="h.missingIndexes.length === 0">{{ h.missingIndexes.length === 0 ? 'Healthy' : h.missingIndexes.length + ' missing' }}</span>
        </div>
        @if (h.missingIndexes.length === 0) {
          <p class="empty">All expected named indexes are present.</p>
        } @else {
          <div class="chip-list">
            @for (name of h.missingIndexes; track name) {
              <code>{{ name }}</code>
            }
          </div>
        }
      </div>

      <div class="section-card">
        <div class="section-header">
          <h3>Offline Indexes</h3>
          <span class="section-state" [class.ok]="h.offlineIndexes.length === 0">{{ h.offlineIndexes.length === 0 ? 'Healthy' : h.offlineIndexes.length + ' issue(s)' }}</span>
        </div>
        @if (h.offlineIndexes.length === 0) {
          <p class="empty">All tracked indexes are online.</p>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Type</th>
                <th>State</th>
                <th>Target</th>
                <th>Failure</th>
              </tr>
            </thead>
            <tbody>
              @for (index of h.offlineIndexes; track index.name) {
                <tr>
                  <td><code>{{ index.name }}</code></td>
                  <td>{{ index.type }}</td>
                  <td>{{ index.state }}</td>
                  <td>{{ formatTarget(index.labelsOrTypes, index.properties) }}</td>
                  <td>{{ index.failureMessage || '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      <div class="section-card">
        <div class="section-header">
          <h3>Duplicate Hot Spots</h3>
          <span class="section-state" [class.ok]="h.duplicateGroups.length === 0">{{ h.duplicateGroups.length === 0 ? 'Healthy' : h.duplicateGroups.length + ' issue group(s)' }}</span>
        </div>
        @if (h.duplicateGroups.length === 0) {
          <p class="empty">No duplicate counter or CodeNode groups were detected in the current scan.</p>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Category</th>
                <th>Key</th>
                <th>Count</th>
                <th>Sample Values</th>
              </tr>
            </thead>
            <tbody>
              @for (group of h.duplicateGroups; track duplicateTrackKey(group)) {
                <tr>
                  <td>{{ group.category }}</td>
                  <td><code>{{ group.key }}</code></td>
                  <td>{{ group.count }}</td>
                  <td>{{ formatSamples(group.sampleValues) }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .page-header {
      display: flex;
      justify-content: space-between;
      gap: 1rem;
      align-items: flex-start;
      margin-bottom: 1rem;
    }
    h2 { margin: 0 0 0.35rem; color: #111827; }
    .subtitle { margin: 0; color: #6b7280; max-width: 60rem; }
    .summary-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 1rem;
      margin-bottom: 1rem;
    }
    .summary-card, .section-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 10px;
      padding: 1rem;
    }
    .summary-label {
      font-size: 0.82rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: #6b7280;
      margin-bottom: 0.35rem;
    }
    .summary-value {
      font-size: 1.6rem;
      font-weight: 700;
      color: #111827;
      margin-bottom: 0.25rem;
    }
    .summary-meta { color: #6b7280; font-size: 0.85rem; }
    .status-pill, .section-state {
      display: inline-flex;
      align-items: center;
      border-radius: 999px;
      padding: 0.2rem 0.65rem;
      font-size: 0.82rem;
      font-weight: 600;
      text-transform: capitalize;
    }
    .status-pill.healthy, .section-state.ok { background: #dcfce7; color: #166534; }
    .status-pill.warning { background: #fef3c7; color: #92400e; }
    .status-pill.critical { background: #fee2e2; color: #991b1b; }
    .section-card { margin-bottom: 1rem; }
    .section-header {
      display: flex;
      justify-content: space-between;
      gap: 1rem;
      align-items: center;
      margin-bottom: 0.75rem;
    }
    .section-header h3 { margin: 0; color: #111827; }
    .empty { margin: 0; color: #4b5563; }
    .chip-list {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
    }
    code {
      background: #f3f4f6;
      border-radius: 4px;
      color: #374151;
      padding: 0.2rem 0.45rem;
      font-size: 0.82rem;
    }
    .data-table {
      width: 100%;
      border-collapse: collapse;
    }
    .data-table th, .data-table td {
      padding: 0.55rem;
      border-bottom: 1px solid #e5e7eb;
      text-align: left;
      vertical-align: top;
      font-size: 0.86rem;
      color: #111827;
    }
    .data-table th { color: #374151; font-weight: 600; }
    .banner {
      border-radius: 8px;
      padding: 0.8rem 1rem;
      margin-bottom: 1rem;
    }
    .banner.error {
      background: #fef2f2;
      border: 1px solid #fecaca;
      color: #991b1b;
    }
    button.primary {
      padding: 0.5rem 0.9rem;
      border-radius: 6px;
      border: none;
      background: #2563eb;
      color: white;
      cursor: pointer;
    }
    button.primary:hover { background: #1d4ed8; }
    button.primary:disabled { opacity: 0.6; cursor: not-allowed; }
  `]
})
export class AdminDbHealthComponent implements OnInit {
  private api = inject(ApiService);

  readonly health = signal<DatabaseHealthResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      this.health.set(await firstValueFrom(this.api.getDatabaseHealth()));
    } catch (err: any) {
      this.error.set(err.error?.message || err.error || err.message || 'Failed to load database health.');
    } finally {
      this.loading.set(false);
    }
  }

  formatTarget(labelsOrTypes: string[], properties: string[]): string {
    const labels = labelsOrTypes.join(', ') || '—';
    const props = properties.length ? ` (${properties.join(', ')})` : '';
    return `${labels}${props}`;
  }

  formatSamples(values: string[]): string {
    return values.length ? values.join(', ') : '—';
  }

  duplicateTrackKey(group: { category: string; key: string }): string {
    return `${group.category}:${group.key}`;
  }
}
