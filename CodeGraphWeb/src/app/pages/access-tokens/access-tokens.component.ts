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
    <div class="page">
      <div class="page-header">
        <div>
          <h1>MCP Access Tokens</h1>
          <p class="subtitle">Issue and revoke personal tokens for external MCP clients.</p>
        </div>
      </div>

      <section class="card">
        <h2>Create Token</h2>
        <div class="form-row">
          <input type="text" [(ngModel)]="newName" placeholder="token name" (keyup.enter)="create()" />
          <select [(ngModel)]="expiresInDays">
            <option [ngValue]="30">30 days</option>
            <option [ngValue]="90">90 days</option>
            <option [ngValue]="180">180 days</option>
            <option [ngValue]="365">365 days</option>
          </select>
          <button type="button" class="primary" (click)="create()" [disabled]="saving()">Create</button>
        </div>

        @if (rawToken()) {
          <div class="token-reveal">
            <div>
              <strong>New token</strong>
              <p>This value is shown once.</p>
            </div>
            <code>{{ rawToken() }}</code>
            <button type="button" (click)="copyRawToken()">Copy</button>
          </div>
        }

        @if (error()) {
          <div class="banner error">{{ error() }}</div>
        }
        @if (success()) {
          <div class="banner success">{{ success() }}</div>
        }
      </section>

      <section class="card">
        <div class="section-header">
          <h2>Current Tokens</h2>
          <span class="count-pill">{{ tokens().length }}</span>
        </div>

        @if (tokens().length === 0) {
          <p class="empty-state">No MCP tokens have been issued.</p>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Prefix</th>
                <th>Status</th>
                <th>Expires</th>
                <th>Last Used</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (token of tokens(); track token.id) {
                <tr>
                  <td>{{ token.tokenName }}</td>
                  <td><code>{{ token.tokenPrefix }}...{{ token.lastFour }}</code></td>
                  <td><span class="status-pill" [attr.data-status]="token.status">{{ token.status }}</span></td>
                  <td>{{ token.expiresAtUtc | date:'mediumDate' }}</td>
                  <td>{{ token.lastUsedAtUtc ? (token.lastUsedAtUtc | date:'short') : 'Never' }}</td>
                  <td class="actions">
                    <button type="button" class="danger" (click)="revoke(token)" [disabled]="saving() || token.status !== 'active'">
                      Revoke
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </section>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .page-header, .section-header, .form-row, .token-reveal {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
      justify-content: space-between;
    }
    .subtitle, .empty-state, .token-reveal p { color: #6b7280; margin: 0.25rem 0 0; }
    h1, h2 { color: #111827; }
    h2 { font-size: 1rem; margin: 0; }
    .form-row { justify-content: flex-start; flex-wrap: wrap; margin-top: 0.85rem; }
    input, select {
      min-height: 36px;
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
      padding: 0.45rem 0.6rem;
    }
    input { width: min(360px, 100%); }
    button {
      min-height: 36px;
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
    .token-reveal {
      align-items: center;
      margin-top: 1rem;
      border: 1px solid #bfdbfe;
      border-radius: 8px;
      background: #eff6ff;
      padding: 0.8rem;
    }
    .token-reveal code {
      flex: 1;
      min-width: 0;
      overflow-wrap: anywhere;
      background: white;
    }
    .count-pill, .status-pill {
      border-radius: 999px;
      background: #f3f4f6;
      color: #374151;
      font-size: 0.78rem;
      font-weight: 700;
      padding: 0.2rem 0.6rem;
      text-transform: capitalize;
    }
    .status-pill[data-status="active"] { background: #dcfce7; color: #166534; }
    .status-pill[data-status="revoked"] { background: #fee2e2; color: #991b1b; }
    .data-table { width: 100%; border-collapse: collapse; margin-top: 0.75rem; }
    .data-table th, .data-table td {
      border-bottom: 1px solid #e5e7eb;
      padding: 0.6rem;
      text-align: left;
      vertical-align: middle;
    }
    .data-table th { color: #374151; font-weight: 600; }
    .actions { text-align: right; }
    .banner { border-radius: 8px; margin-top: 0.75rem; padding: 0.7rem 0.85rem; }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
    .banner.success { background: #f0fdf4; border: 1px solid #bbf7d0; color: #166534; }
    @media (max-width: 720px) {
      .data-table { display: block; overflow-x: auto; }
      .token-reveal { align-items: stretch; flex-direction: column; }
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
