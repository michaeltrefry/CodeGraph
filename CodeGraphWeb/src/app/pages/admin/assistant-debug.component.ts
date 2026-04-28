import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AssistantDebugExchangeListResponse } from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

@Component({
  selector: 'app-assistant-debug',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Assistant Debug Traces</h1>
        <p>Inspect captured provider request and response payloads for a persisted assistant run.</p>
      </div>
      @if (debug(); as data) {
        <span class="cg-chip cg-chip-accent cg-chip-dot">{{ data.exchanges.length }} exchanges</span>
      }
    </header>

    <section class="adm-card debug-lookup-card">
      <header class="debug-section-head">
        <div>
          <span class="adm-section-label">Lookup</span>
          <p>Use a persisted assistant run ID to load the captured provider exchanges.</p>
        </div>
        @if (loading()) {
          <span class="cg-chip cg-chip-accent cg-chip-dot">loading</span>
        }
      </header>

      <div class="debug-lookup-row">
        <label class="adm-field debug-run-field" for="run-id">
          <span class="adm-field-label">Run ID</span>
          <input
            class="adm-input"
            id="run-id"
            type="number"
            min="1"
            [(ngModel)]="runId"
            placeholder="42"
            (keydown.enter)="load()" />
        </label>
        <button class="adm-btn primary" type="button" (click)="load()" [disabled]="loading()">Load traces</button>
      </div>

      <p class="debug-hint">
        Endpoint shape: <code>/api/ask/runs/123/debug-exchanges</code>
      </p>
    </section>

    @if (error()) {
      <div class="adm-banner err">{{ error() }}</div>
    }

    @if (debug(); as data) {
      <section class="adm-card debug-summary-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Run summary</span>
          <span class="cg-chip cg-chip-mono">{{ data.run.status }}</span>
        </header>
        <div class="debug-stat-grid">
          <div class="debug-stat"><span>Run</span><strong>{{ data.run.id }}</strong></div>
          <div class="debug-stat"><span>User</span><strong>{{ data.run.username || 'n/a' }}</strong></div>
          <div class="debug-stat"><span>Chat</span><strong>{{ data.run.chatId || 'n/a' }}</strong></div>
          <div class="debug-stat"><span>Provider</span><strong>{{ data.run.providerUsed || data.run.providerRequested || 'n/a' }}</strong></div>
          <div class="debug-stat"><span>Model</span><strong>{{ data.run.modelUsed || data.run.modelRequested || 'n/a' }}</strong></div>
          <div class="debug-stat"><span>Exchanges</span><strong>{{ data.exchanges.length }}</strong></div>
        </div>
      </section>

      <section class="adm-card debug-payload-card">
        <header class="adm-card-head">
          <span class="adm-section-label">Question</span>
          <span class="cg-chip cg-chip-mono">{{ data.run.createdAt | date:'medium' }}</span>
        </header>
        <pre class="debug-pre">{{ data.run.question }}</pre>
      </section>

      @if (data.exchanges.length === 0) {
        <div class="adm-banner warn">
          No debug exchanges were stored for this run. Debug capture may have been disabled, or the run may not have reached a provider call.
        </div>
      } @else {
        <div class="debug-exchange-list">
          @for (exchange of data.exchanges; track exchange.exchangeIndex) {
            <section class="adm-card debug-exchange-card">
              <header class="debug-exchange-header">
                <div>
                  <span class="adm-section-label">Exchange {{ exchange.exchangeIndex + 1 }}</span>
                  <h2>{{ exchange.provider }} / {{ exchange.model }}</h2>
                  <p>{{ exchange.createdAt | date:'medium' }}</p>
                </div>
                <div class="debug-token-summary" aria-label="Token summary">
                  <span class="cg-chip">Total {{ exchange.totalTokens ?? 0 }}</span>
                  <span class="cg-chip">In {{ exchange.inputTokens ?? 0 }}</span>
                  <span class="cg-chip">Out {{ exchange.outputTokens ?? 0 }}</span>
                </div>
              </header>

              <div class="debug-stat-grid compact">
                <div class="debug-stat"><span>Turn</span><strong>{{ exchange.turnIndex }}</strong></div>
                <div class="debug-stat"><span>Request ID</span><strong>{{ exchange.requestId || 'n/a' }}</strong></div>
                <div class="debug-stat"><span>Response ID</span><strong>{{ exchange.responseId || 'n/a' }}</strong></div>
              </div>

              <div class="debug-text-columns">
                <article class="debug-payload-panel">
                  <h3>Request Text</h3>
                  <pre class="debug-pre">{{ exchange.requestText || '(empty)' }}</pre>
                </article>
                <article class="debug-payload-panel">
                  <h3>Response Text</h3>
                  <pre class="debug-pre">{{ exchange.responseText || '(empty)' }}</pre>
                </article>
              </div>

              <div class="debug-details-list">
                <details class="debug-details">
                  <summary>Request Body JSON</summary>
                  <pre class="debug-pre">{{ formatJson(exchange.requestBody) }}</pre>
                </details>

                @if (exchange.responseBody) {
                  <details class="debug-details">
                    <summary>Response Body JSON</summary>
                    <pre class="debug-pre">{{ formatJson(exchange.responseBody) }}</pre>
                  </details>
                }

                @if (exchange.toolUses) {
                  <details class="debug-details">
                    <summary>Tool Uses JSON</summary>
                    <pre class="debug-pre">{{ formatJson(exchange.toolUses) }}</pre>
                  </details>
                }

                @if (exchange.requestMetadata) {
                  <details class="debug-details">
                    <summary>Request Metadata JSON</summary>
                    <pre class="debug-pre">{{ formatJson(exchange.requestMetadata) }}</pre>
                  </details>
                }

                @if (exchange.responseMetadata) {
                  <details class="debug-details">
                    <summary>Response Metadata JSON</summary>
                    <pre class="debug-pre">{{ formatJson(exchange.responseMetadata) }}</pre>
                  </details>
                }
              </div>
            </section>
          }
        </div>
      }
    }
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      gap: 18px;
    }

    .debug-lookup-card {
      max-width: 720px;
    }

    .debug-section-head,
    .debug-exchange-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 14px;
    }

    .debug-section-head p,
    .debug-exchange-header p,
    .debug-hint {
      color: var(--muted);
      font-size: var(--fs-sm);
      line-height: 1.5;
      margin: 4px 0 0;
    }

    .debug-hint code {
      color: var(--text-2);
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      background: var(--surface-2);
      border: 1px solid var(--hairline);
      border-radius: calc(var(--radius) - 2px);
      padding: 1px 5px;
    }

    .debug-lookup-row {
      display: flex;
      align-items: flex-end;
      gap: 10px;
      flex-wrap: wrap;
    }

    .debug-run-field {
      max-width: 260px;
    }

    .debug-summary-card,
    .debug-payload-card,
    .debug-exchange-card {
      gap: 14px;
    }

    .debug-stat-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
      gap: 10px;
    }

    .debug-stat-grid.compact {
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    }

    .debug-stat {
      min-width: 0;
      border: 1px solid var(--hairline);
      border-radius: var(--radius);
      background: var(--surface-2);
      padding: 10px 12px;
    }

    .debug-stat span {
      display: block;
      color: var(--muted);
      font-size: var(--fs-xs);
      font-weight: 600;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      margin-bottom: 4px;
    }

    .debug-stat strong {
      color: var(--text);
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      font-weight: 600;
      overflow-wrap: anywhere;
    }

    .debug-exchange-list {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }

    .debug-exchange-header h2 {
      color: var(--text);
      font-size: var(--fs-h3);
      font-weight: 600;
      margin: 4px 0 0;
    }

    .debug-token-summary {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }

    .debug-text-columns {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 14px;
    }

    .debug-payload-panel {
      min-width: 0;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .debug-payload-panel h3 {
      color: var(--text);
      font-size: var(--fs-sm);
      font-weight: 600;
      margin: 0;
    }

    .debug-pre {
      max-height: 460px;
      overflow: auto;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      margin: 0;
      padding: 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      color: var(--text);
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.55;
    }

    .debug-details-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .debug-details {
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface);
      overflow: hidden;
    }

    .debug-details summary {
      cursor: pointer;
      color: var(--text-2);
      font-size: var(--fs-sm);
      font-weight: 600;
      padding: 10px 12px;
      transition: background var(--transition), color var(--transition);
    }

    .debug-details summary:hover {
      background: var(--surface-2);
      color: var(--text);
    }

    .debug-details .debug-pre {
      border-width: 1px 0 0;
      border-radius: 0;
      max-height: 520px;
    }

    @media (max-width: 760px) {
      .debug-section-head,
      .debug-exchange-header,
      .debug-lookup-row {
        align-items: stretch;
        flex-direction: column;
      }

      .debug-run-field {
        max-width: none;
      }

      .debug-text-columns {
        grid-template-columns: 1fr;
      }

      .debug-token-summary {
        justify-content: flex-start;
      }
    }
  `]
})
export class AssistantDebugComponent {
  private api = inject(ApiService);

  runId: number | null = null;
  debug = signal<AssistantDebugExchangeListResponse | null>(null);
  loading = signal(false);
  error = signal('');

  formatJson(value: unknown): string {
    if (value === null || value === undefined || value === '') {
      return '(empty)';
    }

    if (typeof value === 'string') {
      try {
        return JSON.stringify(JSON.parse(value), null, 2);
      } catch {
        return value;
      }
    }

    return JSON.stringify(value, null, 2);
  }

  async load(): Promise<void> {
    const id = Number(this.runId);
    this.error.set('');
    this.debug.set(null);

    if (!Number.isInteger(id) || id <= 0) {
      this.error.set('Run id is required.');
      return;
    }

    this.loading.set(true);
    try {
      this.debug.set(await firstValueFrom(this.api.getAssistantDebugExchanges(id)));
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to load assistant debug exchanges'));
    } finally {
      this.loading.set(false);
    }
  }
}
