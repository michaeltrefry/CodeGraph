import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AgentSummary, McpHubToolResponse, McpPersonalAccessTokenMetadata } from '../../core/models';
import { McpToolPickerComponent } from '../access-tokens/mcp-tool-picker.component';
import { extractAdminError, loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

@Component({
  selector: 'app-admin-agents',
  standalone: true,
  imports: [DatePipe, FormsModule, McpToolPickerComponent],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Agents</h1>
        <p>
          Agents are non-human identities for AI harnesses such as Claude or Codex. Their writes are recorded as the
          agent, not as a person. Each agent has its own tokens, and a token's value is shown only once.
        </p>
      </div>
    </header>

    <section class="adm-card">
      <header class="adm-card-head">
        <span class="adm-section-label">Create agent</span>
      </header>
      <div class="adm-form-row">
        <label class="adm-field wide">
          <span class="adm-field-label">Name</span>
          <input class="adm-input" type="text" [(ngModel)]="newName" placeholder="codex" (keyup.enter)="create()" />
          <small class="cg-muted">1-64 characters: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.</small>
        </label>
        <button class="adm-btn primary" type="button" (click)="create()" [disabled]="saving()">Create agent</button>
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
        <span class="adm-section-label">Agents</span>
        <span class="cg-chip cg-chip-mono">{{ agents().length }}</span>
      </header>

      @if (agents().length === 0) {
        <div class="agents-empty cg-muted">No agents yet.</div>
      } @else {
        <ul class="agent-list">
          @for (agent of agents(); track agent.id) {
            <li class="agent-item">
              <div class="agent-row">
                <div class="agent-info">
                  @if (renamingId() === agent.id) {
                    <input class="adm-input" type="text" [(ngModel)]="renameValue" (keyup.enter)="saveRename(agent)" (keyup.escape)="cancelRename()" />
                    <button class="adm-btn primary sm" type="button" (click)="saveRename(agent)" [disabled]="saving()">Save</button>
                    <button class="adm-btn sm" type="button" (click)="cancelRename()" [disabled]="saving()">Cancel</button>
                  } @else {
                    <span class="agent-name cg-mono">{{ agent.name }}</span>
                  }
                  <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="agent.status === 'active'" [class.cg-chip-warn]="agent.status === 'disabled'">
                    {{ agent.status }}
                  </span>
                  <span class="cg-muted cg-small">{{ agent.activeTokens }} active {{ agent.activeTokens === 1 ? 'token' : 'tokens' }}</span>
                  <span class="cg-muted cg-small">Created {{ agent.createdAtUtc | date:'mediumDate' }} by {{ agent.createdBy }}</span>
                </div>
                <div class="agent-actions">
                  <button class="adm-btn sm" type="button" (click)="toggleTokens(agent)">
                    {{ expandedId() === agent.id ? 'Hide tokens' : 'Tokens' }}
                  </button>
                  <button class="adm-btn sm" type="button" (click)="startRename(agent)" [disabled]="saving()">Rename</button>
                  <button class="adm-btn sm" type="button" (click)="setDisabled(agent, agent.status === 'active')" [disabled]="saving()">
                    {{ agent.status === 'active' ? 'Disable' : 'Enable' }}
                  </button>
                  <button class="adm-btn ghost-danger sm" type="button" (click)="remove(agent)" [disabled]="saving()">Delete</button>
                </div>
              </div>

              @if (expandedId() === agent.id) {
                <div class="token-panel">
                  <h3>Issue token for {{ agent.name }}</h3>
                  <div class="adm-form-row">
                    <label class="adm-field wide">
                      <span class="adm-field-label">Token name</span>
                      <input class="adm-input" type="text" [(ngModel)]="tokenName" placeholder="codex - build host" (keyup.enter)="issueToken(agent)" />
                    </label>
                    <label class="adm-field">
                      <span class="adm-field-label">Expiration</span>
                      <select class="adm-input" [(ngModel)]="expiresInDays">
                        <option [ngValue]="30">30 days</option>
                        <option [ngValue]="60">60 days</option>
                        <option [ngValue]="90">90 days</option>
                      </select>
                    </label>
                  </div>
                  <div class="access-mode">
                    <label><input type="radio" [name]="'access-mode-' + agent.id" [checked]="allTools()" (change)="allTools.set(true)" /> All tools</label>
                    <label><input type="radio" [name]="'access-mode-' + agent.id" [checked]="!allTools()" (change)="allTools.set(false)" /> Selected tools</label>
                  </div>
                  @if (!allTools()) {
                    <app-mcp-tool-picker [tools]="tools()" [selected]="selectedToolNames()" (selectedChange)="selectedToolNames.set($event)" />
                  }
                  <div>
                    <button class="adm-btn primary" type="button" (click)="issueToken(agent)" [disabled]="saving()">Issue token</button>
                  </div>

                  @if (rawToken()) {
                    <div class="token-reveal">
                      <div class="reveal-header">
                        <div>
                          <h3>Copy this token now</h3>
                          <p>
                            This is the only time the full value is shown. Set it yourself in the harness config
                            (for example the MCP server's Authorization header); the agent never sees it.
                          </p>
                        </div>
                        <button type="button" class="adm-btn sm" (click)="copyRawToken()">Copy token</button>
                      </div>
                      <pre class="token-secret"><code>{{ rawToken() }}</code></pre>
                    </div>
                  }
                  @if (tokenError()) {
                    <div class="adm-banner err">{{ tokenError() }}</div>
                  }

                  <h3>Tokens</h3>
                  @if (tokens().length === 0) {
                    <div class="agents-empty cg-muted">No tokens issued for this agent.</div>
                  } @else {
                    <div class="table-wrap">
                      <table class="cg-table">
                        <thead>
                          <tr>
                            <th>Token</th>
                            <th>Status</th>
                            <th>Tool access</th>
                            <th>Expires</th>
                            <th>Last used</th>
                            <th></th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (token of tokens(); track token.id) {
                            <tr [class.row-muted]="token.status !== 'active'">
                              <td>
                                <div class="token-name">{{ token.tokenName }}</div>
                                <div class="token-fragment">{{ token.tokenPrefix }}...{{ token.lastFour }}</div>
                              </td>
                              <td>
                                <span
                                  class="cg-chip cg-chip-dot"
                                  [class.cg-chip-ok]="token.status === 'active'"
                                  [class.cg-chip-warn]="token.status === 'expired'"
                                  [class.cg-chip-err]="token.status === 'revoked'">
                                  {{ token.status }}
                                </span>
                              </td>
                              <td>
                                <span class="cg-chip cg-chip-mono">{{ token.entitlementMode }}</span>
                                <small class="cg-muted tool-names">{{ token.toolNames.length ? token.toolNames.join(', ') : 'All registered tools' }}</small>
                              </td>
                              <td>{{ token.expiresAtUtc | date:'mediumDate' }}</td>
                              <td>
                                @if (token.lastUsedAtUtc) {
                                  {{ token.lastUsedAtUtc | date:'medium' }}
                                } @else {
                                  <span class="cg-muted">Never</span>
                                }
                              </td>
                              <td class="cg-cell-actions">
                                <button
                                  type="button"
                                  class="adm-btn sm"
                                  (click)="editingTokenId() === token.id ? cancelEditTools() : startEditTools(token)"
                                  [disabled]="saving() || token.status !== 'active'">
                                  {{ editingTokenId() === token.id ? 'Close' : 'Edit tools' }}
                                </button>
                                <button type="button" class="adm-btn ghost-danger sm" (click)="revoke(agent, token)" [disabled]="saving() || token.status !== 'active'">
                                  Revoke
                                </button>
                              </td>
                            </tr>
                            @if (editingTokenId() === token.id) {
                              <tr>
                                <td colspan="6">
                                  <app-mcp-tool-picker
                                    [heading]="'Edit tool access - ' + token.tokenName"
                                    [tools]="tools()"
                                    [selected]="editToolNames()"
                                    (selectedChange)="editToolNames.set($event)" />
                                  <div class="edit-actions">
                                    <button class="adm-btn primary sm" type="button" (click)="saveEditTools(agent, token)" [disabled]="saving()">Save tools</button>
                                    <button class="adm-btn sm" type="button" (click)="cancelEditTools()" [disabled]="saving()">Cancel</button>
                                  </div>
                                </td>
                              </tr>
                            }
                          }
                        </tbody>
                      </table>
                    </div>
                  }
                </div>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
    .agent-list { list-style: none; padding: 0; margin: 0; }
    .agent-item { border-bottom: 1px solid var(--hairline); }
    .agent-item:last-child { border-bottom: 0; }
    .agent-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 10px 20px;
      flex-wrap: wrap;
    }
    .agent-info { display: flex; align-items: center; gap: 10px; flex: 1; min-width: 0; flex-wrap: wrap; }
    .agent-name { font-size: var(--fs-md); color: var(--text); overflow-wrap: anywhere; }
    .agent-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .agents-empty { padding: 20px; text-align: center; font-size: var(--fs-sm); }
    .token-panel {
      display: grid;
      gap: 14px;
      padding: 16px 20px 20px;
      background: var(--surface-2);
      border-top: 1px solid var(--hairline);
    }
    .token-panel h3 { margin: 0; color: var(--text); font-size: var(--fs-h4); }
    .access-mode { display: flex; gap: 18px; flex-wrap: wrap; }
    .token-reveal {
      display: grid;
      gap: 12px;
      border: 1px solid var(--accent-dim);
      border-radius: var(--radius-lg);
      background: var(--accent-weak);
      padding: 14px;
    }
    .reveal-header { display: flex; justify-content: space-between; gap: 12px; align-items: flex-start; }
    .reveal-header p { margin: 4px 0 0; color: var(--muted); line-height: 1.5; }
    .token-secret {
      margin: 0;
      max-height: 160px;
      overflow: auto;
      border: 1px solid var(--accent-dim);
      border-radius: var(--radius);
      background: var(--surface);
      color: var(--text);
      padding: 12px;
      white-space: pre-wrap;
    }
    code, .token-fragment { font-family: var(--font-mono); }
    .token-name { color: var(--text); font-weight: 600; }
    .token-fragment { font-size: var(--fs-sm); overflow-wrap: anywhere; }
    .tool-names { display: block; overflow-wrap: anywhere; font-size: var(--fs-xs); }
    .row-muted { opacity: 0.72; }
    .table-wrap { overflow-x: auto; }
    .edit-actions { display: flex; gap: 8px; margin-top: 10px; }
  `]
})
export class AdminAgentsComponent implements OnInit {
  private api = inject(ApiService);

  agents = signal<AgentSummary[]>([]);
  error = signal('');
  success = signal('');
  saving = signal(false);
  newName = '';

  renamingId = signal<number | null>(null);
  renameValue = '';

  expandedId = signal<number | null>(null);
  tokens = signal<McpPersonalAccessTokenMetadata[]>([]);
  tools = signal<McpHubToolResponse[]>([]);
  tokenError = signal('');
  rawToken = signal('');
  tokenName = '';
  expiresInDays = 90;
  allTools = signal(true);
  selectedToolNames = signal<ReadonlySet<string>>(new Set());
  editingTokenId = signal<number | null>(null);
  editToolNames = signal<ReadonlySet<string>>(new Set());

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    await loadAdminCollection(
      () => firstValueFrom(this.api.listAgents()),
      this.agents,
      this.error,
      'Failed to load agents');
  }

  async create(): Promise<void> {
    const name = this.newName.trim();
    this.error.set('');
    this.success.set('');
    if (!name) {
      this.error.set('Agent name is required.');
      return;
    }

    this.saving.set(true);
    try {
      const created = await runAdminMutation(
        () => firstValueFrom(this.api.createAgent(name)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Created agent ${name.toLowerCase()}.`,
          fallbackError: 'Failed to create agent'
        });
      if (created) {
        this.newName = '';
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  startRename(agent: AgentSummary): void {
    this.renamingId.set(agent.id);
    this.renameValue = agent.name;
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  async saveRename(agent: AgentSummary): Promise<void> {
    const name = this.renameValue.trim();
    if (!name) {
      this.error.set('Agent name is required.');
      return;
    }

    this.saving.set(true);
    try {
      const renamed = await runAdminMutation(
        () => firstValueFrom(this.api.updateAgent(agent.id, { name })),
        {
          error: this.error,
          success: this.success,
          successMessage: `Renamed ${agent.name} to ${name.toLowerCase()}.`,
          fallbackError: 'Failed to rename agent'
        });
      if (renamed) {
        this.renamingId.set(null);
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  async setDisabled(agent: AgentSummary, disabled: boolean): Promise<void> {
    this.saving.set(true);
    try {
      const updated = await runAdminMutation(
        () => firstValueFrom(this.api.updateAgent(agent.id, { disabled })),
        {
          error: this.error,
          success: this.success,
          successMessage: `${disabled ? 'Disabled' : 'Enabled'} ${agent.name}.`,
          fallbackError: `Failed to ${disabled ? 'disable' : 'enable'} agent`
        });
      if (updated) await this.load();
    } finally {
      this.saving.set(false);
    }
  }

  async remove(agent: AgentSummary): Promise<void> {
    if (!confirm(`Delete agent '${agent.name}'? Its tokens stop working.`)) return;

    this.saving.set(true);
    try {
      const removed = await runAdminMutation(
        () => firstValueFrom(this.api.deleteAgent(agent.id)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Deleted ${agent.name}.`,
          fallbackError: 'Failed to delete agent'
        });
      if (removed) {
        if (this.expandedId() === agent.id) this.collapseTokens();
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  async toggleTokens(agent: AgentSummary): Promise<void> {
    if (this.expandedId() === agent.id) {
      this.collapseTokens();
      return;
    }

    this.collapseTokens();
    this.expandedId.set(agent.id);
    await this.loadTokens(agent.id);
    if (this.tools().length === 0) {
      try {
        const tools = await firstValueFrom(this.api.listUserMcpTools());
        this.tools.set(tools);
        this.selectedToolNames.set(new Set(tools.filter(tool => tool.enabled && tool.readOnly).map(tool => tool.toolName)));
      } catch (err) {
        this.tokenError.set(extractAdminError(err, 'Failed to load MCP tools'));
      }
    }
  }

  private collapseTokens(): void {
    this.expandedId.set(null);
    this.tokens.set([]);
    this.rawToken.set('');
    this.tokenError.set('');
    this.editingTokenId.set(null);
    this.tokenName = '';
  }

  private async loadTokens(agentId: number): Promise<void> {
    await loadAdminCollection(
      () => firstValueFrom(this.api.listAgentTokens(agentId)),
      this.tokens,
      this.tokenError,
      'Failed to load agent tokens');
  }

  async issueToken(agent: AgentSummary): Promise<void> {
    const name = this.tokenName.trim();
    this.tokenError.set('');
    this.success.set('');
    this.rawToken.set('');

    if (!name) {
      this.tokenError.set('Token name is required.');
      return;
    }
    const selected = [...this.selectedToolNames()].sort();
    if (!this.allTools() && selected.length === 0) {
      this.tokenError.set('Select at least one MCP tool.');
      return;
    }

    this.saving.set(true);
    try {
      const created = await firstValueFrom(
        this.api.createAgentToken(agent.id, name, this.expiresInDays, this.allTools() ? undefined : selected));
      this.rawToken.set(created.rawToken);
      this.success.set(`Issued ${created.token.tokenName} for ${agent.name}.`);
      this.tokenName = '';
      await Promise.all([this.loadTokens(agent.id), this.load()]);
    } catch (err) {
      this.tokenError.set(extractAdminError(err, 'Failed to issue token'));
    } finally {
      this.saving.set(false);
    }
  }

  startEditTools(token: McpPersonalAccessTokenMetadata): void {
    this.editingTokenId.set(token.id);
    this.editToolNames.set(new Set(token.toolNames));
  }

  cancelEditTools(): void {
    this.editingTokenId.set(null);
  }

  async saveEditTools(agent: AgentSummary, token: McpPersonalAccessTokenMetadata): Promise<void> {
    const selected = [...this.editToolNames()].sort();
    if (selected.length === 0) {
      this.tokenError.set('Select at least one MCP tool.');
      return;
    }

    this.saving.set(true);
    try {
      const updated = await runAdminMutation(
        () => firstValueFrom(this.api.updateAgentTokenTools(agent.id, token.id, selected)),
        {
          error: this.tokenError,
          success: this.success,
          successMessage: `Updated tool access for ${token.tokenName}.`,
          fallbackError: 'Failed to update token tools'
        });
      if (updated) {
        this.editingTokenId.set(null);
        await this.loadTokens(agent.id);
      }
    } finally {
      this.saving.set(false);
    }
  }

  async revoke(agent: AgentSummary, token: McpPersonalAccessTokenMetadata): Promise<void> {
    if (!confirm(`Revoke '${token.tokenName}'?`)) return;

    this.saving.set(true);
    try {
      const revoked = await runAdminMutation(
        () => firstValueFrom(this.api.revokeAgentToken(agent.id, token.id)),
        {
          error: this.tokenError,
          success: this.success,
          successMessage: `Revoked ${token.tokenName}.`,
          fallbackError: 'Failed to revoke token'
        });
      if (revoked) await Promise.all([this.loadTokens(agent.id), this.load()]);
    } finally {
      this.saving.set(false);
    }
  }

  async copyRawToken(): Promise<void> {
    const token = this.rawToken();
    if (!token) return;
    await navigator.clipboard.writeText(token);
    this.success.set('Token copied.');
  }
}
