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
    <header class="adm-page-header">
      <div>
        <h1>DB health</h1>
        <p>Check for schema drift, offline indexes, and duplicate hot spots that can hurt insert performance.</p>
      </div>
      <button class="adm-btn primary" type="button" (click)="load()" [disabled]="loading()">
        {{ loading() ? 'Refreshing…' : 'Refresh' }}
      </button>
    </header>

    @if (error()) {
      <div class="adm-banner err">{{ error() }}</div>
    }

    @if (health(); as h) {
      <div class="summary-grid">
        <article class="adm-card summary-card">
          <div class="summary-label">Overall</div>
          <div class="summary-value">
            <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="h.status === 'healthy'" [class.cg-chip-warn]="h.status === 'warning'" [class.cg-chip-err]="h.status === 'critical'">
              {{ h.status }}
            </span>
          </div>
          <div class="summary-meta">Captured {{ h.capturedAt | date:'medium' }}</div>
        </article>

        <article class="adm-card summary-card">
          <div class="summary-label">Constraints</div>
          <div class="summary-value">{{ h.constraintCount }}/{{ h.expectedConstraintCount }}</div>
          <div class="summary-meta">{{ h.missingConstraints.length }} missing</div>
        </article>

        <article class="adm-card summary-card">
          <div class="summary-label">Indexes</div>
          <div class="summary-value">{{ h.indexCount }}/{{ h.expectedIndexCount }}</div>
          <div class="summary-meta">{{ h.missingIndexes.length }} missing, {{ h.offlineIndexes.length }} offline</div>
        </article>

        <article class="adm-card summary-card">
          <div class="summary-label">Duplicate Hot Spots</div>
          <div class="summary-value">{{ h.duplicateGroups.length }}</div>
          <div class="summary-meta">Top 50 issue groups</div>
        </article>
      </div>

      <section class="adm-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Missing constraints</span>
          <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="h.missingConstraints.length === 0" [class.cg-chip-warn]="h.missingConstraints.length > 0">
            {{ h.missingConstraints.length === 0 ? 'healthy' : h.missingConstraints.length + ' missing' }}
          </span>
        </header>
        @if (h.missingConstraints.length === 0) {
          <p class="empty cg-muted">All expected named constraints are present.</p>
        } @else {
          <div class="chip-list">
            @for (name of h.missingConstraints; track name) {
              <span class="cg-chip cg-chip-mono">{{ name }}</span>
            }
          </div>
        }
      </section>

      <section class="adm-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Missing indexes</span>
          <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="h.missingIndexes.length === 0" [class.cg-chip-warn]="h.missingIndexes.length > 0">
            {{ h.missingIndexes.length === 0 ? 'healthy' : h.missingIndexes.length + ' missing' }}
          </span>
        </header>
        @if (h.missingIndexes.length === 0) {
          <p class="empty cg-muted">All expected named indexes are present.</p>
        } @else {
          <div class="chip-list">
            @for (name of h.missingIndexes; track name) {
              <span class="cg-chip cg-chip-mono">{{ name }}</span>
            }
          </div>
        }
      </section>

      <section class="adm-card adm-card-flush">
        <header class="adm-card-head">
          <span class="adm-section-label">Offline indexes</span>
          <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="h.offlineIndexes.length === 0" [class.cg-chip-err]="h.offlineIndexes.length > 0">
            {{ h.offlineIndexes.length === 0 ? 'healthy' : h.offlineIndexes.length + ' issue(s)' }}
          </span>
        </header>
        @if (h.offlineIndexes.length === 0) {
          <p class="empty cg-muted">All tracked indexes are online.</p>
        } @else {
          <table class="cg-table">
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
                  <td><span class="cg-mono cg-small">{{ index.name }}</span></td>
                  <td>{{ index.type }}</td>
                  <td>{{ index.state }}</td>
                  <td>{{ formatTarget(index.labelsOrTypes, index.properties) }}</td>
                  <td>{{ index.failureMessage || '—' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
      </section>

      <section class="adm-card adm-card-flush">
        <header class="adm-card-head">
          <span class="adm-section-label">Duplicate hot spots</span>
          <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="h.duplicateGroups.length === 0" [class.cg-chip-warn]="h.duplicateGroups.length > 0">
            {{ h.duplicateGroups.length === 0 ? 'healthy' : h.duplicateGroups.length + ' issue group(s)' }}
          </span>
        </header>
        @if (h.duplicateGroups.length === 0) {
          <p class="empty cg-muted">No duplicate counter or CodeNode groups were detected in the current scan.</p>
        } @else {
          <table class="cg-table">
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
                  <td><span class="cg-mono cg-small">{{ group.key }}</span></td>
                  <td class="cg-cell-num">{{ group.count }}</td>
                  <td>{{ formatSamples(group.sampleValues) }}</td>
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

    .summary-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 14px;
    }

    .summary-label {
      font-size: var(--fs-xs);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--muted);
    }

    .summary-value {
      font-size: 26px;
      font-weight: 700;
      color: var(--text);
    }

    .summary-meta {
      color: var(--muted);
      font-size: var(--fs-sm);
    }

    .empty {
      margin: 0;
      padding: 16px 20px;
    }

    .chip-list {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
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
