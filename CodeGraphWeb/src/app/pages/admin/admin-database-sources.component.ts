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
    <header class="adm-page-header">
      <div>
        <h1>Database sources</h1>
        <p>Configure external databases whose schemas can be indexed by the standalone indexer.</p>
      </div>
      <button type="button" class="adm-btn" (click)="load()" [disabled]="saving()">Refresh</button>
    </header>

    <section class="adm-card db-key-card">
      <header class="db-card-head">
        <div>
          <h2>Encryption key</h2>
          <p>Generate a base64 AES-256 key for CodeGraph:StorageOptions:MariaDbEncryptionKey.</p>
        </div>
      </header>
      <div class="key-row">
        <button class="adm-btn" type="button" (click)="generateKey()">Generate Key</button>
        @if (generatedKey()) {
          <span class="key-value cg-mono">{{ generatedKey() }}</span>
          <button class="adm-btn" type="button" (click)="copyKey()">{{ copyLabel() }}</button>
        }
      </div>
    </section>

    <section class="adm-card">
      <header class="adm-card-head">
        <span class="adm-section-label">Add source</span>
      </header>
      <div class="adm-form-row db-form-row">
        <label class="adm-field">
          <span class="adm-field-label">Server</span>
          <input class="adm-input" type="text" [(ngModel)]="newServerName" placeholder="analytics-db" />
        </label>
        <label class="adm-field">
          <span class="adm-field-label">Database</span>
          <input class="adm-input" type="text" [(ngModel)]="newDatabaseName" placeholder="blank for all" />
        </label>
        <label class="adm-field wide">
          <span class="adm-field-label">Connection string</span>
          <input class="adm-input" type="text" [(ngModel)]="newConnectionString" placeholder="Server=...;Database=...;" />
        </label>
        <button class="adm-btn primary" type="button" (click)="create()" [disabled]="saving()">Add source</button>
      </div>

      @if (error()) {
        <div class="adm-banner err">{{ error() }}</div>
      }
      @if (success()) {
        <div class="adm-banner ok">{{ success() }}</div>
      }
    </section>

    <section class="adm-card adm-card-flush">
      <header class="adm-card-head">
        <span class="adm-section-label">Configured sources</span>
        <div class="section-actions">
          <span class="cg-chip cg-chip-mono">{{ sources().length }}</span>
          <button class="adm-btn primary" type="button" (click)="syncAll()" [disabled]="saving() || sources().length === 0">Sync All</button>
        </div>
      </header>

      <div class="table-wrap">
        <table class="cg-table">
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
                <td><span class="cg-mono cg-small">{{ source.serverName }}</span></td>
                <td><span class="cg-mono cg-small">{{ source.databaseName || '(all)' }}</span></td>
                <td class="connection"><span class="cg-mono cg-small">{{ source.connectionString }}</span></td>
                <td>
                  <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="source.enabled" [class.cg-chip-warn]="!source.enabled">
                    {{ source.enabled ? 'Enabled' : 'Disabled' }}
                  </span>
                </td>
                <td>{{ source.lastSyncedAt ? (source.lastSyncedAt | date:'short') : 'Never' }}</td>
                <td>{{ lastRunMessage() || 'None queued' }}</td>
                <td class="cg-cell-actions">
                  <button class="adm-btn primary sm" type="button" (click)="sync(source)" [disabled]="saving() || !source.enabled">Sync</button>
                  <button class="adm-btn sm" type="button" (click)="toggle(source)" [disabled]="saving()">{{ source.enabled ? 'Disable' : 'Enable' }}</button>
                  <button class="adm-btn ghost-danger sm" type="button" (click)="remove(source)" [disabled]="saving()">Delete</button>
                </td>
              </tr>
            }
            @if (sources().length === 0) {
              <tr>
                <td colspan="7" class="empty cg-muted">No database sources configured.</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
    .key-row,
    .db-card-head {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
      justify-content: space-between;
    }

    .db-card-head h2 {
      color: var(--text);
      font-size: var(--fs-h3);
      margin: 0;
    }

    .db-card-head p {
      color: var(--muted);
      margin: 4px 0 0;
    }

    .db-form-row .wide {
      flex-basis: 100%;
    }

    .key-row { justify-content: flex-start; flex-wrap: wrap; margin-top: 0.75rem; }
    .key-value {
      flex: 1 1 360px;
      min-width: 0;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      color: var(--accent-ink);
      padding: 0.45rem 0.6rem;
      overflow-wrap: anywhere;
    }
    .table-wrap { overflow-x: auto; }
    .cg-table { min-width: 820px; }
    .section-actions { display: flex; align-items: center; gap: 0.5rem; }
    .connection { max-width: 320px; }
    .empty { text-align: center; }
    @media (max-width: 760px) {
      .adm-card-head,
      .db-card-head {
        align-items: stretch;
        flex-direction: column;
      }
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
