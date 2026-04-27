import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { DatabaseSourceResponse } from '../../core/models';
import { extractAdminError, loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

@Component({
  selector: 'app-admin-database-sources',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <div class="page-header">
      <div>
        <h2>Database Sources</h2>
        <p class="subtitle">Configure external databases whose schemas can be indexed later by the standalone indexer.</p>
      </div>
      <button type="button" class="primary" (click)="load()" [disabled]="saving()">Refresh</button>
    </div>

    <section class="section-card">
      <div class="section-header">
        <div>
          <h3>Encryption Key</h3>
          <p class="muted">Generate a base64 AES-256 key for CodeGraph:StorageOptions:MariaDbEncryptionKey.</p>
        </div>
      </div>
      <div class="key-row">
        <button type="button" (click)="generateKey()">Generate Key</button>
        @if (generatedKey()) {
          <code class="key-value">{{ generatedKey() }}</code>
          <button type="button" (click)="copyKey()">{{ copyLabel() }}</button>
        }
      </div>
    </section>

    <section class="section-card">
      <h3>Add Source</h3>
      <div class="form-grid">
        <label>
          <span>Server</span>
          <input type="text" [(ngModel)]="newServerName" placeholder="analytics-db" />
        </label>
        <label>
          <span>Database</span>
          <input type="text" [(ngModel)]="newDatabaseName" placeholder="blank for all" />
        </label>
        <label class="wide">
          <span>Connection string</span>
          <input type="text" [(ngModel)]="newConnectionString" placeholder="Server=...;Database=...;" />
        </label>
        <button class="primary" type="button" (click)="create()" [disabled]="saving()">Add</button>
      </div>

      @if (error()) {
        <div class="banner error">{{ error() }}</div>
      }
      @if (success()) {
        <div class="banner success">{{ success() }}</div>
      }
    </section>

    <section class="section-card">
      <div class="section-header">
        <h3>Configured Sources</h3>
        <div class="section-actions">
          <span class="count-pill">{{ sources().length }}</span>
          <button type="button" (click)="syncAll()" [disabled]="saving() || sources().length === 0">Sync All</button>
        </div>
      </div>

      <div class="table-wrap">
        <table class="data-table">
          <thead>
            <tr>
              <th>Server</th>
              <th>Database</th>
              <th>Connection</th>
              <th>Status</th>
              <th>Last Synced</th>
              <th>Last Run</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (source of sources(); track source.id) {
              <tr>
                <td><code>{{ source.serverName }}</code></td>
                <td><code>{{ source.databaseName || '(all)' }}</code></td>
                <td class="connection"><code>{{ source.connectionString }}</code></td>
                <td>
                  <span class="status-pill" [class.active]="source.enabled">
                    {{ source.enabled ? 'Enabled' : 'Disabled' }}
                  </span>
                </td>
                <td>{{ source.lastSyncedAt ? (source.lastSyncedAt | date:'short') : 'Never' }}</td>
                <td>{{ lastRunMessage() || 'None queued' }}</td>
                <td class="actions">
                  <button type="button" (click)="sync(source)" [disabled]="saving() || !source.enabled">Sync</button>
                  <button type="button" (click)="toggle(source)" [disabled]="saving()">{{ source.enabled ? 'Disable' : 'Enable' }}</button>
                  <button type="button" class="danger" (click)="remove(source)" [disabled]="saving()">Delete</button>
                </td>
              </tr>
            }
            @if (sources().length === 0) {
              <tr>
                <td colspan="7" class="empty">No database sources configured.</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .page-header, .section-header, .key-row {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
      justify-content: space-between;
    }
    .page-header { margin-bottom: 1rem; }
    h2, h3 { margin: 0; color: #111827; }
    .subtitle, .muted { margin: 0.35rem 0 0; color: #6b7280; }
    .section-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 1rem;
      margin-bottom: 1rem;
    }
    .form-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(180px, 1fr));
      gap: 0.75rem;
      align-items: end;
      margin-top: 0.75rem;
    }
    label { display: flex; flex-direction: column; gap: 0.25rem; color: #374151; font-weight: 600; }
    label.wide { grid-column: 1 / -1; }
    input {
      padding: 0.5rem 0.6rem;
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
      min-width: 0;
    }
    button {
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: white;
      color: #374151;
      cursor: pointer;
      padding: 0.45rem 0.8rem;
    }
    button.primary { background: #2563eb; border-color: #2563eb; color: white; }
    button.danger { color: #991b1b; border-color: #fecaca; }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .key-row { justify-content: flex-start; flex-wrap: wrap; margin-top: 0.75rem; }
    .key-value {
      flex: 1 1 360px;
      min-width: 0;
      border: 1px solid #e5e7eb;
      border-radius: 6px;
      padding: 0.45rem 0.6rem;
    }
    .table-wrap { overflow-x: auto; margin-top: 0.75rem; }
    .data-table { width: 100%; min-width: 820px; border-collapse: collapse; }
    .data-table th, .data-table td {
      border-bottom: 1px solid #e5e7eb;
      padding: 0.6rem;
      text-align: left;
      vertical-align: top;
    }
    .data-table th { color: #374151; font-weight: 600; }
    .section-actions { display: flex; align-items: center; gap: 0.5rem; }
    .connection { max-width: 320px; }
    .actions { display: flex; justify-content: flex-end; gap: 0.4rem; }
    .count-pill, .status-pill {
      border-radius: 999px;
      background: #f3f4f6;
      color: #374151;
      flex: 0 0 auto;
      font-size: 0.78rem;
      font-weight: 700;
      padding: 0.2rem 0.6rem;
    }
    .status-pill.active { background: #dcfce7; color: #166534; }
    .banner { border-radius: 8px; margin-top: 0.75rem; padding: 0.7rem 0.85rem; }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
    .banner.success { background: #f0fdf4; border: 1px solid #bbf7d0; color: #166534; }
    .empty { color: #6b7280; text-align: center; }
    code { overflow-wrap: anywhere; white-space: normal; }
    @media (max-width: 760px) {
      .page-header, .section-header { flex-direction: column; }
      .form-grid { grid-template-columns: 1fr; }
    }
  `]
})
export class AdminDatabaseSourcesComponent implements OnInit {
  private api = inject(ApiService);

  sources = signal<DatabaseSourceResponse[]>([]);
  generatedKey = signal('');
  copyLabel = signal('Copy');
  error = signal('');
  success = signal('');
  saving = signal(false);
  lastRunMessage = signal('');

  newServerName = '';
  newDatabaseName = '';
  newConnectionString = '';

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    await loadAdminCollection(
      () => firstValueFrom(this.api.listDatabaseSources()),
      this.sources,
      this.error,
      'Failed to load database sources');
  }

  async generateKey(): Promise<void> {
    try {
      const result = await firstValueFrom(this.api.generateDatabaseSourceKey());
      this.generatedKey.set(result.key);
      this.copyLabel.set('Copy');
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to generate key'));
    }
  }

  copyKey(): void {
    navigator.clipboard.writeText(this.generatedKey());
    this.copyLabel.set('Copied');
    setTimeout(() => this.copyLabel.set('Copy'), 2000);
  }

  async create(): Promise<void> {
    const serverName = this.newServerName.trim();
    const databaseName = this.newDatabaseName.trim();
    const connectionString = this.newConnectionString.trim();

    if (!serverName || !connectionString) {
      this.error.set('Server and connection string are required.');
      return;
    }

    this.saving.set(true);
    try {
      const created = await runAdminMutation(
        () => firstValueFrom(this.api.createDatabaseSource({
          serverName,
          databaseName: databaseName || null,
          connectionString,
          enabled: true
        })),
        {
          error: this.error,
          success: this.success,
          successMessage: `Added ${serverName}.`,
          fallbackError: 'Failed to create database source'
        });
      if (created) {
        this.newServerName = '';
        this.newDatabaseName = '';
        this.newConnectionString = '';
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  async toggle(source: DatabaseSourceResponse): Promise<void> {
    this.saving.set(true);
    try {
      const updated = await runAdminMutation(
        () => firstValueFrom(this.api.updateDatabaseSource(source.id, { enabled: !source.enabled })),
        {
          error: this.error,
          success: this.success,
          successMessage: `${source.serverName} ${source.enabled ? 'disabled' : 'enabled'}.`,
          fallbackError: 'Failed to update database source'
        });
      if (updated) await this.load();
    } finally {
      this.saving.set(false);
    }
  }

  async sync(source: DatabaseSourceResponse): Promise<void> {
    this.error.set('');
    this.success.set('');
    this.saving.set(true);
    try {
      const accepted = await firstValueFrom(this.api.syncDatabaseSource(source.id));
      this.success.set(`Queued schema sync for ${source.serverName}.`);
      this.lastRunMessage.set(this.formatRunMessage(accepted));
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to queue schema sync'));
    } finally {
      this.saving.set(false);
    }
  }

  async syncAll(): Promise<void> {
    this.error.set('');
    this.success.set('');
    this.saving.set(true);
    try {
      const accepted = await firstValueFrom(this.api.syncAllDatabaseSources());
      this.success.set('Queued schema sync for all enabled sources.');
      this.lastRunMessage.set(this.formatRunMessage(accepted));
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to queue schema sync'));
    } finally {
      this.saving.set(false);
    }
  }

  async remove(source: DatabaseSourceResponse): Promise<void> {
    if (!confirm(`Remove database source '${source.serverName}'?`)) return;

    this.saving.set(true);
    try {
      const removed = await runAdminMutation(
        () => firstValueFrom(this.api.deleteDatabaseSource(source.id)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Removed ${source.serverName}.`,
          fallbackError: 'Failed to delete database source'
        });
      if (removed) await this.load();
    } finally {
      this.saving.set(false);
    }
  }

  private formatRunMessage(accepted: { runId?: number; message?: string }): string {
    return accepted.runId
      ? `Run #${accepted.runId}`
      : accepted.message || 'Queued';
  }
}
