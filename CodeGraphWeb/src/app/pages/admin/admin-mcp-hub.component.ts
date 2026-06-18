import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { McpHubAuditResponse, McpHubCatalogResponse, McpHubConfigResponse, McpHubCredentialResponse } from '../../core/models';
import { extractAdminError, runAdminMutation } from './admin-resource.helpers';

@Component({
  selector: 'app-admin-mcp-hub',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <section class="cg-page admin-mcp-page">
      <header class="cg-page-header">
        <p class="adm-eyebrow">Admin</p>
        <h1>MCP Hub</h1>
        <p>Manage MCP providers, exact tool entitlements, shared provider credentials, and provider operations.</p>
      </header>

      @if (error()) {
        <div class="adm-banner err">{{ error() }}</div>
      }
      @if (success()) {
        <div class="adm-banner ok">{{ success() }}</div>
      }

      <section class="cg-card cg-card-padded">
        <header class="section-head">
          <div>
            <h2>Providers</h2>
            <p>{{ catalog()?.providers?.length || 0 }} providers / {{ catalog()?.tools?.length || 0 }} tools</p>
          </div>
          <button type="button" class="cg-btn secondary" (click)="load()">Refresh</button>
        </header>

        <div class="provider-grid">
          @for (provider of catalog()?.providers || []; track provider.providerKey) {
            <article class="provider-tile">
              <div>
                <h3>{{ provider.displayName }}</h3>
                <p>{{ provider.description }}</p>
              </div>
              <label class="toggle-row">
                <input type="checkbox" [checked]="provider.enabled" (change)="setProviderEnabled(provider.providerKey, $any($event.target).checked)" />
                <span>{{ provider.enabled ? 'Enabled' : 'Disabled' }}</span>
              </label>
            </article>
          }
        </div>
      </section>

      <section class="cg-card cg-card-padded">
        <header class="section-head">
          <div>
            <h2>Tool Catalog</h2>
            <p>Disabled tools cannot be called by any token.</p>
          </div>
        </header>

        <div class="table-wrap">
          <table class="cg-table">
            <thead>
              <tr>
                <th>Tool</th>
                <th>Provider</th>
                <th>Access class</th>
                <th>Available</th>
                <th>Default</th>
                <th>Credential</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (tool of catalog()?.tools || []; track tool.toolName) {
                <tr>
                  <td>
                    <strong>{{ tool.displayName }}</strong>
                    <small>{{ tool.toolName }}</small>
                  </td>
                  <td>{{ tool.providerKey }}</td>
                  <td>{{ tool.accessClass }}</td>
                  <td>{{ tool.isAvailable ? 'Yes' : 'Unavailable' }}</td>
                  <td>
                    <button type="button" class="cg-btn secondary" (click)="setToolDefaultSelected(tool.toolName, !tool.defaultSelected)">
                      {{ tool.defaultSelected ? 'Default on' : 'Default off' }}
                    </button>
                  </td>
                  <td>{{ tool.requiresCredential ? 'Required' : 'No' }}</td>
                  <td class="cg-cell-actions">
                    <button type="button" class="cg-btn secondary" (click)="setToolEnabled(tool.toolName, !tool.enabled)">
                      {{ tool.enabled ? 'Disable' : 'Enable' }}
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <section class="two-col">
        <article class="cg-card cg-card-padded">
          <header class="section-head">
            <div>
              <h2>Credentials</h2>
              <p>Values are encrypted at rest and never returned after save.</p>
            </div>
          </header>
          <div class="form-list">
            @for (item of credentialRows; track item.providerKey + item.credentialKey) {
              <label class="cg-field">
                <span class="cg-field-label">{{ item.providerKey }} / {{ item.credentialKey }}</span>
                <div class="inline-form">
                  <input class="cg-input" type="password" [(ngModel)]="item.value" [placeholder]="credentialPlaceholder(item)" />
                  <button type="button" class="cg-btn primary" (click)="saveCredential(item)">Save</button>
                </div>
              </label>
            }
          </div>
        </article>

        <article class="cg-card cg-card-padded">
          <header class="section-head">
            <div>
              <h2>Config</h2>
              <p>Provider settings used by hub shims.</p>
            </div>
          </header>
          <div class="form-list">
            @for (item of configRows; track item.providerKey + item.configKey) {
              <label class="cg-field">
                <span class="cg-field-label">{{ item.providerKey }} / {{ item.configKey }}</span>
                <div class="inline-form">
                  <input class="cg-input" type="text" [(ngModel)]="item.value" />
                  <button type="button" class="cg-btn primary" (click)="saveConfig(item)">Save</button>
                </div>
              </label>
            }
          </div>
        </article>
      </section>

      <section class="cg-card">
        <header class="section-head padded">
          <div>
            <h2>Audit</h2>
            <p>Recent hub provider calls.</p>
          </div>
        </header>
        <div class="table-wrap">
          <table class="cg-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Tool</th>
                <th>Resource</th>
                <th>User</th>
                <th>Status</th>
                <th>Duration</th>
                <th>Message</th>
              </tr>
            </thead>
            <tbody>
              @for (row of audit(); track row.id) {
                <tr>
                  <td>{{ row.createdAtUtc | date:'medium' }}</td>
                  <td>
                    <strong>{{ row.toolName }}</strong>
                    <small>{{ row.operation }} / {{ row.credentialMode }}</small>
                  </td>
                  <td>{{ row.resourceKey || '' }}</td>
                  <td>{{ row.username || 'unknown' }}</td>
                  <td>{{ row.statusClass || (row.success ? 'ok' : 'failed') }}</td>
                  <td>{{ row.durationMs }} ms</td>
                  <td>{{ row.message || '' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    </section>
  `,
  styles: [`
    .admin-mcp-page { display: grid; gap: 18px; }
    .section-head {
      display: flex;
      justify-content: space-between;
      gap: 16px;
      align-items: flex-start;
      margin-bottom: 16px;
    }
    .section-head.padded { padding: 16px 20px; margin-bottom: 0; border-bottom: 1px solid var(--border); }
    .section-head h2, .provider-tile h3 { margin: 0; color: var(--text); }
    .section-head p, .provider-tile p, td small { margin: 4px 0 0; color: var(--muted); }
    .provider-grid, .two-col {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: 12px;
    }
    .provider-tile {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 14px;
      background: var(--surface-2);
    }
    .toggle-row, .inline-form {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .form-list { display: grid; gap: 14px; }
    .inline-form .cg-input { min-width: 0; }
    .table-wrap { overflow-x: auto; }
    td strong, td small { display: block; overflow-wrap: anywhere; }
    @media (max-width: 720px) {
      .section-head, .provider-tile, .inline-form { flex-direction: column; align-items: stretch; }
    }
  `]
})
export class AdminMcpHubComponent implements OnInit {
  private api = inject(ApiService);

  catalog = signal<McpHubCatalogResponse | null>(null);
  credentials = signal<McpHubCredentialResponse[]>([]);
  config = signal<McpHubConfigResponse[]>([]);
  audit = signal<McpHubAuditResponse[]>([]);
  error = signal('');
  success = signal('');

  credentialRows = [
    { providerKey: 'shortcut', credentialKey: 'apiToken', value: '' },
    { providerKey: 'rabbitmq', credentialKey: 'username', value: '' },
    { providerKey: 'rabbitmq', credentialKey: 'password', value: '' },
    { providerKey: 'mysql', credentialKey: 'connectionString', value: '' }
  ];

  configRows = [
    { providerKey: 'rabbitmq', configKey: 'managementBaseUrl', value: '' },
    { providerKey: 'rabbitmq', configKey: 'allowedQueues', value: '' },
    { providerKey: 'mysql', configKey: 'allowedSources', value: '' },
    { providerKey: 'mysql', configKey: 'sensitiveColumnPattern', value: '' }
  ];

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.error.set('');
    try {
      const [catalog, credentials, config, audit] = await Promise.all([
        firstValueFrom(this.api.getMcpHubCatalog()),
        firstValueFrom(this.api.listMcpHubCredentials()),
        firstValueFrom(this.api.listMcpHubConfig()),
        firstValueFrom(this.api.listMcpHubAudit(50))
      ]);
      this.catalog.set(catalog);
      this.credentials.set(credentials);
      this.config.set(config);
      this.audit.set(audit);
      for (const row of this.configRows) {
        row.value = config.find(item => item.providerKey === row.providerKey && item.configKey === row.configKey)?.configValue || '';
      }
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to load MCP Hub settings'));
    }
  }

  async setProviderEnabled(providerKey: string, enabled: boolean): Promise<void> {
    await this.mutate(() => this.api.updateMcpHubProvider(providerKey, { enabled }), 'Provider updated.');
  }

  async setToolEnabled(toolName: string, enabled: boolean): Promise<void> {
    await this.mutate(() => this.api.updateMcpHubTool(toolName, { enabled }), 'Tool updated.');
  }

  async setToolDefaultSelected(toolName: string, defaultSelected: boolean): Promise<void> {
    await this.mutate(() => this.api.updateMcpHubTool(toolName, { defaultSelected }), 'Tool updated.');
  }

  async saveCredential(row: { providerKey: string; credentialKey: string; value: string }): Promise<void> {
    if (!row.value.trim()) {
      this.error.set('Credential value is required.');
      return;
    }
    await this.mutate(() => this.api.setMcpHubCredential(row.providerKey, row.credentialKey, row.value), 'Credential saved.');
    row.value = '';
  }

  async saveConfig(row: { providerKey: string; configKey: string; value: string }): Promise<void> {
    await this.mutate(() => this.api.setMcpHubConfig(row.providerKey, row.configKey, row.value || null), 'Config saved.');
  }

  credentialPlaceholder(row: { providerKey: string; credentialKey: string }): string {
    const existing = this.credentials().find(item => item.providerKey === row.providerKey && item.credentialKey === row.credentialKey);
    return existing?.hasValue ? 'Configured' : 'Not configured';
  }

  private async mutate(operation: () => ReturnType<ApiService['updateMcpHubTool']>, message: string): Promise<void> {
    const changed = await runAdminMutation(
      () => firstValueFrom(operation()),
      { error: this.error, success: this.success, successMessage: message, fallbackError: 'MCP Hub update failed' });
    if (changed) await this.load();
  }
}
