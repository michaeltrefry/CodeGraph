import { Component, inject, OnInit, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const API = environment.apiUrl;

interface JobSchedule {
  id: number;
  name: string;
  jobType: string;
  isEnabled: boolean;
  cronExpression: string;
  timeZoneId: string;
  args: any;
  nextRunUtc: string;
  lastRunStartedUtc: string | null;
  lastRunCompletedUtc: string | null;
  lastRunStatus: string | null;
  lastError: string | null;
  isRunning: boolean;
}

interface JobExecutionResult {
  success: boolean;
  message: string;
  startedAtUtc: string;
  completedAtUtc: string;
}

interface JobTypeOption {
  value: string;
  label: string;
  description: string;
}

@Component({
  selector: 'app-admin-schedules',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Schedules</h1>
        <p>Create recurring schedules for maintenance and indexing jobs. Execution timestamps are stored in UTC.</p>
      </div>
      <button class="adm-btn" type="button" (click)="load()" [disabled]="loading()">Refresh</button>
    </header>

    @if (error()) {
      <div class="adm-banner err">{{ error() }}</div>
    }
    @if (success()) {
      <div class="adm-banner ok">{{ success() }}</div>
    }

    <div class="schedule-layout">
      <section class="adm-card editor-card">
        <header class="adm-card-head">
          <span class="adm-section-label">{{ editingId() ? 'Edit schedule' : 'New schedule' }}</span>
        </header>
        <label class="adm-field">
          <span class="adm-field-label">Name</span>
          <input class="adm-input" type="text" [(ngModel)]="name" placeholder="Nightly discovery" />
        </label>

        <label class="adm-field">
          <span class="adm-field-label">Job</span>
          <select class="adm-select" [(ngModel)]="jobType" (ngModelChange)="onJobTypeChanged()">
            @for (option of jobTypes; track option.value) {
              <option [value]="option.value">{{ option.label }}</option>
            }
          </select>
        </label>

        <p class="job-help">{{ currentJobDescription() }}</p>

        <label class="adm-field">
          <span class="adm-field-label">Cron</span>
          <input class="adm-input" type="text" [(ngModel)]="cronExpression" placeholder="0 */6 * * *" />
        </label>

        <label class="adm-field">
          <span class="adm-field-label">Time zone</span>
          <input class="adm-input" type="text" [(ngModel)]="timeZoneId" placeholder="America/New_York" />
        </label>

        <label class="adm-checkbox checkbox">
          <input type="checkbox" [(ngModel)]="isEnabled" />
          <span>Enabled</span>
        </label>

        @if (jobType === 'Discover') {
          <div class="args-grid">
            <label class="adm-checkbox checkbox">
              <input type="checkbox" [(ngModel)]="discoverShouldIndex" />
              <span>Index</span>
            </label>
            <label class="adm-checkbox checkbox">
              <input type="checkbox" [(ngModel)]="discoverShouldAnalyze" />
              <span>Analyze</span>
            </label>
            <label class="adm-checkbox checkbox">
              <input type="checkbox" [(ngModel)]="discoverSkipIfUpToDate" />
              <span>Skip up-to-date</span>
            </label>
            <label class="adm-checkbox checkbox">
              <input type="checkbox" [(ngModel)]="discoverIncludeAllSource" />
              <span>Include all source</span>
            </label>
            <label class="adm-field">
              <span class="adm-field-label">Name regex</span>
              <input class="adm-input" type="text" [(ngModel)]="discoverNamePattern" placeholder="orders|billing" />
            </label>
            <label class="adm-field">
              <span class="adm-field-label">Limit</span>
              <input class="adm-input" type="number" [(ngModel)]="discoverLimit" min="1" />
            </label>
          </div>
        }

        @if (jobType === 'ProcessBatchAnalysis') {
          <label class="adm-field">
            <span class="adm-field-label">Repo filter</span>
            <input class="adm-input" type="text" [(ngModel)]="batchRepo" placeholder="optional repo name" />
          </label>
        }

        <div class="editor-actions">
          <button class="adm-btn primary" type="button" (click)="save()" [disabled]="saving()">{{ editingId() ? 'Update' : 'Create' }}</button>
          <button class="adm-btn" type="button" (click)="resetForm()" [disabled]="saving()">Clear</button>
        </div>
      </section>

      <section class="adm-card adm-card-flush list-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Saved schedules</span>
          <span class="cg-chip cg-chip-mono">{{ schedules().length }}</span>
        </header>

        <table class="cg-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Job</th>
              <th>Status</th>
              <th>Next Run</th>
              <th>Last Result</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            @for (schedule of schedules(); track schedule.id) {
              <tr>
                <td>
                  <div class="name-cell">
                    <strong>{{ schedule.name }}</strong>
                    <span class="cg-muted cg-small">{{ schedule.cronExpression }} · {{ schedule.timeZoneId }}</span>
                  </div>
                </td>
                <td>{{ schedule.jobType }}</td>
                <td>
                  <span class="cg-chip cg-chip-dot"
                        [class.cg-chip-accent]="schedule.isRunning"
                        [class.cg-chip-ok]="schedule.isEnabled && !schedule.isRunning"
                        [class.cg-chip-warn]="!schedule.isEnabled">
                    {{ schedule.isRunning ? 'Running' : (schedule.isEnabled ? 'Enabled' : 'Disabled') }}
                  </span>
                </td>
                <td>{{ schedule.nextRunUtc | date:'short' }}</td>
                <td>
                  <div class="result-cell">
                    <span>{{ schedule.lastRunStatus || 'Never run' }}</span>
                    @if (schedule.lastRunCompletedUtc) {
                      <span class="cg-muted cg-small">{{ schedule.lastRunCompletedUtc | date:'short' }}</span>
                    }
                    @if (schedule.lastError) {
                      <span class="error-inline">{{ schedule.lastError }}</span>
                    }
                  </div>
                </td>
                <td>
                  <div class="action-group">
                    <button class="adm-btn sm" type="button" (click)="edit(schedule)">Edit</button>
                    <button class="adm-btn sm" type="button" (click)="runNow(schedule)" [disabled]="schedule.isRunning">Run now</button>
                    <button class="adm-btn sm" type="button" (click)="toggleEnabled(schedule)">{{ schedule.isEnabled ? 'Disable' : 'Enable' }}</button>
                    <button class="adm-btn ghost-danger sm" type="button" (click)="remove(schedule)">Delete</button>
                  </div>
                </td>
              </tr>
            }
            @if (schedules().length === 0) {
              <tr>
                <td colspan="6" class="empty cg-muted">No schedules configured yet.</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    </div>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
    .schedule-layout { display: grid; grid-template-columns: minmax(320px, 380px) minmax(0, 1fr); gap: 16px; align-items: start; }
    .editor-card .adm-field { flex: 0 1 auto; }
    .job-help { margin: 0; color: var(--muted); font-size: var(--fs-sm); }
    .args-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }
    .editor-actions { display: flex; gap: 8px; margin-top: 4px; }
    .name-cell, .result-cell { display: flex; flex-direction: column; gap: 0.2rem; }
    .action-group { display: flex; flex-wrap: wrap; gap: 0.35rem; }
    .error-inline { color: var(--sem-red); font-size: var(--fs-xs); }
    .empty { text-align: center; }
    @media (max-width: 1100px) {
      .schedule-layout { grid-template-columns: 1fr; }
    }
  `]
})
export class AdminSchedulesComponent implements OnInit {
  private http = inject(HttpClient);

  readonly jobTypes: JobTypeOption[] = [
    { value: 'Discover', label: 'Discover', description: 'Discover repositories from the configured source and publish new ones for indexing/analysis.' },
    { value: 'ReIndexAll', label: 'Re-Index All', description: 'Publish all known repositories for a fresh indexing pass.' },
    { value: 'ProcessBatchAnalysis', label: 'Process Batch Analysis', description: 'Poll pending analysis batches and store completed results.' },
    { value: 'LinkAndDetect', label: 'Link And Detect', description: 'Run cross-repo linking and community detection in one pass.' },
    { value: 'DetectCommunities', label: 'Detect Communities', description: 'Re-run community detection using the current graph edges.' },
    { value: 'RegenerateMcpDocs', label: 'Regenerate MCP Docs', description: 'Rebuild MCP documentation wiki pages from current tool metadata.' }
  ];

  schedules = signal<JobSchedule[]>([]);
  loading = signal(false);
  saving = signal(false);
  editingId = signal<number | null>(null);
  error = signal('');
  success = signal('');

  name = '';
  jobType = 'Discover';
  cronExpression = '0 */6 * * *';
  timeZoneId = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  isEnabled = true;

  discoverShouldIndex = true;
  discoverShouldAnalyze = true;
  discoverSkipIfUpToDate = true;
  discoverIncludeAllSource = false;
  discoverNamePattern = '';
  discoverLimit: number | null = null;
  batchRepo = '';

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  currentJobDescription(): string {
    return this.jobTypes.find(x => x.value === this.jobType)?.description ?? '';
  }

  onJobTypeChanged(): void {
    this.error.set('');
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const schedules = await firstValueFrom(this.http.get<JobSchedule[]>(`${API}/settings/schedules`));
      this.schedules.set(schedules);
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to load schedules.');
    } finally {
      this.loading.set(false);
    }
  }

  edit(schedule: JobSchedule): void {
    this.editingId.set(schedule.id);
    this.name = schedule.name;
    this.jobType = schedule.jobType;
    this.cronExpression = schedule.cronExpression;
    this.timeZoneId = schedule.timeZoneId;
    this.isEnabled = schedule.isEnabled;

    const args = schedule.args || {};
    this.discoverShouldIndex = args.shouldIndex ?? true;
    this.discoverShouldAnalyze = args.shouldAnalyze ?? true;
    this.discoverSkipIfUpToDate = args.skipIfUpToDate ?? true;
    this.discoverIncludeAllSource = args.includeAllSource ?? false;
    this.discoverNamePattern = args.namePattern ?? '';
    this.discoverLimit = args.limit ?? null;
    this.batchRepo = args.repo ?? '';
    this.success.set('');
    this.error.set('');
  }

  resetForm(): void {
    this.editingId.set(null);
    this.name = '';
    this.jobType = 'Discover';
    this.cronExpression = '0 */6 * * *';
    this.timeZoneId = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    this.isEnabled = true;
    this.discoverShouldIndex = true;
    this.discoverShouldAnalyze = true;
    this.discoverSkipIfUpToDate = true;
    this.discoverIncludeAllSource = false;
    this.discoverNamePattern = '';
    this.discoverLimit = null;
    this.batchRepo = '';
    this.error.set('');
  }

  async save(): Promise<void> {
    this.saving.set(true);
    this.error.set('');
    this.success.set('');

    const body = {
      name: this.name.trim(),
      jobType: this.jobType,
      isEnabled: this.isEnabled,
      cronExpression: this.cronExpression.trim(),
      timeZoneId: this.timeZoneId.trim() || 'UTC',
      args: this.buildArgs()
    };

    try {
      if (this.editingId()) {
        await firstValueFrom(this.http.put(`${API}/settings/schedules/${this.editingId()}`, body));
        this.success.set('Schedule updated.');
      } else {
        await firstValueFrom(this.http.post(`${API}/settings/schedules`, body));
        this.success.set('Schedule created.');
      }

      await this.load();
      this.resetForm();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to save schedule.');
    } finally {
      this.saving.set(false);
    }
  }

  async runNow(schedule: JobSchedule): Promise<void> {
    this.error.set('');
    this.success.set('');

    try {
      const result = await firstValueFrom(this.http.post<JobExecutionResult>(`${API}/settings/schedules/${schedule.id}/run`, {}));
      this.success.set(result.message);
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to run schedule.');
    }
  }

  async toggleEnabled(schedule: JobSchedule): Promise<void> {
    this.error.set('');
    this.success.set('');

    try {
      const endpoint = schedule.isEnabled ? 'disable' : 'enable';
      await firstValueFrom(this.http.post(`${API}/settings/schedules/${schedule.id}/${endpoint}`, {}));
      this.success.set(`Schedule ${schedule.isEnabled ? 'disabled' : 'enabled'}.`);
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to update schedule.');
    }
  }

  async remove(schedule: JobSchedule): Promise<void> {
    if (!confirm(`Delete schedule "${schedule.name}"?`)) return;

    this.error.set('');
    this.success.set('');
    try {
      await firstValueFrom(this.http.delete(`${API}/settings/schedules/${schedule.id}`));
      this.success.set('Schedule deleted.');
      if (this.editingId() === schedule.id) this.resetForm();
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to delete schedule.');
    }
  }

  private buildArgs(): any {
    switch (this.jobType) {
      case 'Discover':
        return {
          shouldIndex: this.discoverShouldIndex,
          shouldAnalyze: this.discoverShouldAnalyze,
          skipIfUpToDate: this.discoverSkipIfUpToDate,
          includeAllSource: this.discoverIncludeAllSource,
          namePattern: this.discoverNamePattern.trim() || null,
          limit: this.discoverLimit && this.discoverLimit > 0 ? this.discoverLimit : null
        };
      case 'ProcessBatchAnalysis':
        return {
          repo: this.batchRepo.trim() || null
        };
      default:
        return {};
    }
  }
}
