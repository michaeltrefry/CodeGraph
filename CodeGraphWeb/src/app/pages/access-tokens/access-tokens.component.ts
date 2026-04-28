import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { McpPersonalAccessTokenMetadata } from '../../core/models';
import { extractAdminError, loadAdminCollection, runAdminMutation } from '../admin/admin-resource.helpers';

@Component({
  selector: 'app-access-tokens',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <section class="cg-page access-page">
      <header class="cg-page-header access-header">
        <div class="access-header-copy">
          <p class="access-eyebrow">Settings</p>
          <h1>Access Tokens</h1>
          <p>
            Create personal access tokens for MCP clients. Tokens work for external clients and do not replace normal
            website sign-in.
          </p>
        </div>

        <div class="access-summary-grid">
          <article class="access-summary-card">
            <div class="summary-value">{{ activeTokenCount() }}</div>
            <div class="summary-label">Active tokens</div>
            <p>{{ tokens().length }} total issued</p>
          </article>
          <article class="access-summary-card">
            <div class="summary-value">Bearer</div>
            <div class="summary-label">MCP auth</div>
            <p><code>Authorization: Bearer &lt;token&gt;</code></p>
          </article>
        </div>
      </header>

      <div class="top-grid">
        <section class="cg-card cg-card-padded access-card">
          <div class="access-card-header">
            <h2>Create Token</h2>
            <p>The raw token is shown once right after creation, so copy it before you leave this page.</p>
          </div>

          <div class="form-grid">
            <label class="cg-field">
              <span class="cg-field-label">Friendly name</span>
              <input class="cg-input" type="text" [(ngModel)]="newName" placeholder="Claude Desktop - Work Laptop" (keyup.enter)="create()" />
              <small>Use the machine or client name so it is easy to revoke later.</small>
            </label>
            <label class="cg-field">
              <span class="cg-field-label">Expiration</span>
              <select class="cg-select" [(ngModel)]="expiresInDays">
                <option [ngValue]="30">30 days</option>
                <option [ngValue]="90">90 days</option>
                <option [ngValue]="180">180 days</option>
                <option [ngValue]="365">365 days</option>
              </select>
              <small>Choose a shorter lifetime for shared or temporary clients.</small>
            </label>
          </div>

          <div class="button-row">
            <button type="button" class="cg-btn primary" (click)="create()" [disabled]="saving()">
              {{ saving() ? 'Creating...' : 'Create token' }}
            </button>
          </div>

          @if (rawToken()) {
            <div class="token-reveal">
              <div class="reveal-header">
                <div>
                  <h3>Copy this token now</h3>
                  <p>This is the only time the full token value will be shown.</p>
                </div>
                <button type="button" class="cg-btn secondary" (click)="copyRawToken()">Copy token</button>
              </div>
              <pre class="token-secret"><code>{{ rawToken() }}</code></pre>
            </div>
          }

          @if (error()) {
            <div class="adm-banner err">{{ error() }}</div>
          }
          @if (success()) {
            <div class="adm-banner ok">{{ success() }}</div>
          }
        </section>

        <section class="cg-card cg-card-padded access-card">
          <div class="access-card-header">
            <h2>Using a Token</h2>
            <p>Each token is tied to your CodeGraph identity, so MCP memory tools automatically scope to you.</p>
          </div>
          <ol class="guide-list">
            <li>Create one token per machine or client so old devices can be revoked cleanly.</li>
            <li>Store the raw token in the MCP client configuration, not in source control.</li>
            <li>Revoke tokens you no longer recognize or use.</li>
          </ol>
        </section>
      </div>

      <section class="cg-card access-table-card">
        <header class="access-card-header access-table-header">
          <div>
            <h2>Your Tokens</h2>
            <p>Active, expired, and revoked tokens stay visible here so you can audit usage.</p>
          </div>
          <span class="cg-chip cg-chip-mono">{{ tokens().length }}</span>
        </header>

        @if (tokens().length === 0) {
          <div class="state-message">No MCP access tokens yet.</div>
        } @else {
          <div class="table-wrap">
            <table class="cg-table token-table">
              <thead>
                <tr>
                  <th>Token</th>
                  <th>Status</th>
                  <th>Created</th>
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
                    <td>{{ token.createdAtUtc | date:'mediumDate' }}</td>
                    <td>{{ token.expiresAtUtc | date:'mediumDate' }}</td>
                    <td>
                      @if (token.lastUsedAtUtc) {
                        <div>{{ token.lastUsedAtUtc | date:'medium' }}</div>
                      } @else {
                        <div class="cg-muted">Never</div>
                      }
                      <div class="last-used-from">{{ token.lastUsedFrom || 'Origin unavailable' }}</div>
                    </td>
                    <td class="cg-cell-actions">
                      <button type="button" class="cg-btn danger" (click)="revoke(token)" [disabled]="saving() || token.status !== 'active'">
                        Revoke
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    </section>
  `,
  styles: [`
    :host {
      display: flex;
      min-height: 100%;
      background: var(--bg);
    }

    .access-header {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(280px, 360px);
      gap: 16px;
      align-items: start;
    }

    .access-header-copy {
      min-width: 0;
    }

    .access-eyebrow {
      margin: 0 0 6px;
      color: var(--accent-ink);
      font-size: var(--fs-xs);
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }

    .access-summary-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }

    .access-summary-card {
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      background: var(--surface);
      padding: 16px;
    }

    .summary-value {
      font-family: var(--font-mono);
      font-size: var(--fs-h1);
      font-weight: 600;
      line-height: 1;
      color: var(--text);
    }

    .summary-label {
      color: var(--muted);
      font-size: var(--fs-xs);
      font-weight: 600;
      letter-spacing: 0.05em;
      text-transform: uppercase;
    }

    .access-summary-card p,
    .access-card-header p,
    .cg-field small,
    .guide-list,
    .last-used-from {
      color: var(--muted);
    }

    .top-grid {
      display: grid;
      grid-template-columns: minmax(0, 1.2fr) minmax(320px, 0.8fr);
      gap: 16px;
      align-items: start;
    }

    .access-card {
      display: grid;
      gap: 18px;
    }

    .access-card-header {
      margin-bottom: 18px;
    }

    .access-table-header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 12px;
      padding: 16px 20px;
      border-bottom: 1px solid var(--border);
      margin-bottom: 0;
    }

    .access-card-header h2,
    .reveal-header h3 {
      color: var(--text);
      font-size: var(--fs-h3);
      margin: 0;
    }

    .access-card-header p,
    .reveal-header p,
    .access-summary-card p {
      margin: 4px 0 0;
      line-height: 1.5;
    }

    .form-grid {
      display: grid;
      gap: 16px;
    }

    .button-row {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      align-items: center;
    }

    .token-reveal {
      display: grid;
      gap: 12px;
      border: 1px solid var(--accent-dim);
      border-radius: var(--radius-lg);
      background: var(--accent-weak);
      padding: 14px;
    }

    .reveal-header {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      align-items: flex-start;
    }

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

    code,
    .token-fragment {
      font-family: var(--font-mono);
    }

    .token-name {
      color: var(--text);
      font-weight: 600;
    }

    .token-fragment,
    .last-used-from {
      font-size: var(--fs-sm);
      overflow-wrap: anywhere;
    }

    .row-muted {
      opacity: 0.72;
    }

    .table-wrap {
      overflow-x: auto;
    }

    .token-table {
      min-width: 760px;
    }

    .state-message {
      padding: 28px 20px;
      text-align: center;
      color: var(--muted);
    }

    @media (max-width: 720px) {
      .access-header,
      .top-grid,
      .access-summary-grid {
        grid-template-columns: 1fr;
      }

      .reveal-header,
      .access-table-header {
        flex-direction: column;
      }
    }
  `]
})
export class AccessTokensComponent implements OnInit {
  private api = inject(ApiService);

  tokens = signal<McpPersonalAccessTokenMetadata[]>([]);
  error = signal('');
  success = signal('');
  rawToken = signal('');
  saving = signal(false);
  newName = '';
  expiresInDays = 90;

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    await loadAdminCollection(
      () => firstValueFrom(this.api.listMcpTokens()),
      this.tokens,
      this.error,
      'Failed to load MCP tokens');
  }

  activeTokenCount(): number {
    return this.tokens().filter(token => token.status === 'active').length;
  }

  async create(): Promise<void> {
    const name = this.newName.trim();
    this.error.set('');
    this.success.set('');
    this.rawToken.set('');

    if (!name) {
      this.error.set('Token name is required.');
      return;
    }

    this.saving.set(true);
    try {
      const created = await firstValueFrom(this.api.createMcpToken(name, this.expiresInDays));
      this.rawToken.set(created.rawToken);
      this.success.set(`Created ${created.token.tokenName}.`);
      this.newName = '';
      await this.load();
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to create MCP token'));
    } finally {
      this.saving.set(false);
    }
  }

  async revoke(token: McpPersonalAccessTokenMetadata): Promise<void> {
    if (!confirm(`Revoke '${token.tokenName}'?`)) return;

    this.saving.set(true);
    try {
      const revoked = await runAdminMutation(
        () => firstValueFrom(this.api.revokeMcpToken(token.id)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Revoked ${token.tokenName}.`,
          fallbackError: 'Failed to revoke MCP token'
        });
      if (revoked) await this.load();
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
