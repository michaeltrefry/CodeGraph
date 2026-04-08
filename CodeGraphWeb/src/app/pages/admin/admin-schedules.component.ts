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
    <h2>Schedules</h2>
    <p class="subtitle">Create recurring schedules for built-in maintenance and indexing jobs. All execution timestamps are stored in UTC.</p>

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
    @if (success()) {
      <p class="success">{{ success() }}</p>
    }

    <div class="schedule-layout">
      <section class="editor-card">
        <h3>{{ editingId() ? 'Edit Schedule' : 'New Schedule' }}</h3>
        <label>
          <span>Name</span>
          <input type="text" [(ngModel)]="name" placeholder="Nightly discovery" />
        </label>

        <label>
          <span>Job</span>
          <select [(ngModel)]="jobType" (ngModelChange)="onJobTypeChanged()">
            @for (option of jobTypes; track option.value) {
              <option [value]="option.value">{{ option.label }}</option>
            }
          </select>
        </label>

        <p class="job-help">{{ currentJobDescription() }}</p>

        <label>
          <span>Cron</span>
          <input type="text" [(ngModel)]="cronExpression" placeholder="0 */6 * * *" />
        </label>

        <label>
          <span>Time Zone</span>
          <input type="text" [(ngModel)]="timeZoneId" placeholder="America/New_York" />
        </label>

        <label class="checkbox">
          <input type="checkbox" [(ngModel)]="isEnabled" />
          <span>Enabled</span>
        </label>

        @if (jobType === 'Discover') {
          <div class="args-grid">
            <label class="checkbox">
              <input type="checkbox" [(ngModel)]="discoverShouldIndex" />
              <span>Index</span>
            </label>
            <label class="checkbox">
              <input type="checkbox" [(ngModel)]="discoverShouldAnalyze" />
              <span>Analyze</span>
            </label>
            <label class="checkbox">
              <input type="checkbox" [(ngModel)]="discoverSkipIfUpToDate" />
              <span>Skip up-to-date</span>
            </label>
            <label class="checkbox">
              <input type="checkbox" [(ngModel)]="discoverIncludeAllSource" />
              <span>Include all source</span>
            </label>
            <label>
              <span>Name Regex</span>
              <input type="text" [(ngModel)]="discoverNamePattern" placeholder="orders|billing" />
            </label>
            <label>
              <span>Limit</span>
              <input type="number" [(ngModel)]="discoverLimit" min="1" />
            </label>
          </div>
        }

        @if (jobType === 'ProcessBatchAnalysis') {
          <label>
            <span>Repo Filter</span>
            <input type="text" [(ngModel)]="batchRepo" placeholder="optional repo name" />
          </label>
        }

        <div class="editor-actions">
          <button class="primary" (click)="save()" [disabled]="saving()">{{ editingId() ? 'Update' : 'Create' }}</button>
          <button (click)="resetForm()" [disabled]="saving()">Clear</button>
        </div>
      </section>

      <section class="list-card">
        <div class="list-header">
          <h3>Saved Schedules</h3>
          <button (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        <table class="schedule-table">
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
                    <span class="meta">{{ schedule.cronExpression }} · {{ schedule.timeZoneId }}</span>
                  </div>
                </td>
                <td>{{ schedule.jobType }}</td>
                <td>
                  <span class="badge" [class.badge-running]="schedule.isRunning" [class.badge-enabled]="schedule.isEnabled && !schedule.isRunning" [class.badge-disabled]="!schedule.isEnabled">
                    {{ schedule.isRunning ? 'Running' : (schedule.isEnabled ? 'Enabled' : 'Disabled') }}
                  </span>
                </td>
                <td>{{ schedule.nextRunUtc | date:'short' }}</td>
                <td>
                  <div class="result-cell">
                    <span>{{ schedule.lastRunStatus || 'Never run' }}</span>
                    @if (schedule.lastRunCompletedUtc) {
                      <span class="meta">{{ schedule.lastRunCompletedUtc | date:'short' }}</span>
                    }
                    @if (schedule.lastError) {
                      <span class="error-inline">{{ schedule.lastError }}</span>
                    }
                  </div>
                </td>
                <td>
                  <div class="action-group">
                    <button (click)="edit(schedule)">Edit</button>
                    <button (click)="runNow(schedule)" [disabled]="schedule.isRunning">Run now</button>
                    <button (click)="toggleEnabled(schedule)">{{ schedule.isEnabled ? 'Disable' : 'Enable' }}</button>
                    <button class="danger" (click)="remove(schedule)">Delete</button>
                  </div>
                </td>
              </tr>
            }
            @if (schedules().length === 0) {
              <tr>
                <td colspan="6" class="muted">No schedules configured yet.</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    </div>
  `,
  styles: [`
    .subtitle { color: #6b7280; font-size: 0.92rem; margin-bottom: 1rem; }
    .schedule-layout { display: grid; grid-template-columns: minmax(320px, 380px) minmax(0, 1fr); gap: 1rem; align-items: start; }
    .editor-card, .list-card {
      background: white; border: 1px solid #e5e7eb; border-radius: 10px;
      padding: 1rem;
    }
    .editor-card h3, .list-card h3 { margin: 0 0 0.9rem; color: #111827; }
    label { display: block; margin-bottom: 0.75rem; }
    label span { display: block; font-size: 0.8rem; color: #374151; margin-bottom: 0.25rem; }
    input[type="text"], input[type="number"], select {
      width: 100%; padding: 0.45rem 0.55rem;
      border-radius: 6px; border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827; font-family: inherit;
    }
    .checkbox {
      display: flex; align-items: center; gap: 0.45rem;
      margin-bottom: 0.5rem;
    }
    .checkbox span { margin: 0; }
    .job-help { margin: -0.2rem 0 0.75rem; color: #6b7280; font-size: 0.82rem; }
    .args-grid {
      display: grid; grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.55rem 0.75rem;
      margin-bottom: 0.75rem;
    }
    .editor-actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
    .list-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .schedule-table { width: 100%; border-collapse: collapse; }
    .schedule-table th, .schedule-table td {
      padding: 0.65rem 0.5rem; text-align: left; border-bottom: 1px solid #e5e7eb;
      vertical-align: top; font-size: 0.84rem; color: #111827;
    }
    .schedule-table th { color: #374151; font-weight: 600; }
    .name-cell, .result-cell { display: flex; flex-direction: column; gap: 0.2rem; }
    .meta { color: #6b7280; font-size: 0.76rem; }
    .badge {
      display: inline-block; padding: 0.18rem 0.48rem; border-radius: 999px;
      font-size: 0.75rem; font-weight: 600;
    }
    .badge-enabled { background: #dcfce7; color: #166534; }
    .badge-disabled { background: #f3f4f6; color: #4b5563; }
    .badge-running { background: #dbeafe; color: #1d4ed8; }
    .action-group { display: flex; flex-wrap: wrap; gap: 0.35rem; }
    button {
      padding: 0.38rem 0.72rem; border-radius: 6px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer; font-size: 0.8rem;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
    button.danger { background: #dc2626; border-color: transparent; color: white; }
    button.danger:hover { background: #b91c1c; }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .error { color: #dc2626; margin-bottom: 0.75rem; }
    .success { color: #059669; margin-bottom: 0.75rem; }
    .error-inline { color: #dc2626; font-size: 0.76rem; }
    .muted { text-align: center; color: #9ca3af; }
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
