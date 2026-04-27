import { DatePipe, JsonPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AssistantDebugExchangeListResponse } from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

@Component({
  selector: 'app-assistant-debug',
  standalone: true,
  imports: [DatePipe, FormsModule, JsonPipe],
  template: `
    <div class="page-header">
      <div>
        <h2>Assistant Debug</h2>
        <p class="subtitle">Inspect captured provider exchanges for a persisted assistant run.</p>
      </div>
    </div>

    <section class="section-card lookup">
      <input type="number" min="1" [(ngModel)]="runId" placeholder="run id" (keyup.enter)="load()" />
      <button type="button" class="primary" (click)="load()" [disabled]="loading()">Load</button>
    </section>

    @if (loading()) {
      <div class="section-card muted">Loading debug exchanges...</div>
    } @else if (error()) {
      <div class="banner error">{{ error() }}</div>
    } @else if (debug(); as data) {
      <section class="section-card">
        <div class="section-header">
          <div>
            <h3>Run {{ data.run.id }}</h3>
            <p class="subtitle">{{ data.run.status }} · {{ data.run.createdAt | date:'medium' }}</p>
          </div>
          <span class="count-pill">{{ data.exchanges.length }} exchanges</span>
        </div>
        <p class="question">{{ data.run.question }}</p>
      </section>

      @for (exchange of data.exchanges; track exchange.exchangeIndex) {
        <section class="section-card exchange-card">
          <div class="section-header">
            <h3>#{{ exchange.exchangeIndex }} {{ exchange.provider }} / {{ exchange.model }}</h3>
            <span class="muted">{{ exchange.createdAt | date:'medium' }}</span>
          </div>
          <div class="token-row">
            <span>Input {{ exchange.inputTokens ?? 0 }}</span>
            <span>Output {{ exchange.outputTokens ?? 0 }}</span>
            <span>Total {{ exchange.totalTokens ?? 0 }}</span>
          </div>
          <div class="debug-grid">
            <div>
              <h4>Request</h4>
              <pre>{{ exchange.requestText }}</pre>
            </div>
            <div>
              <h4>Response</h4>
              <pre>{{ exchange.responseText || 'No response text captured.' }}</pre>
            </div>
          </div>
          <details>
            <summary>Raw bodies</summary>
            <pre>{{ { request: exchange.requestBody, response: exchange.responseBody, toolUses: exchange.toolUses } | json }}</pre>
          </details>
        </section>
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .page-header, .lookup, .section-header, .token-row {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
      justify-content: space-between;
    }
    .page-header { margin-bottom: 1rem; }
    h2, h3, h4 { margin: 0; color: #111827; }
    h4 { font-size: 0.88rem; margin-bottom: 0.35rem; }
    .subtitle, .muted { color: #6b7280; margin: 0.25rem 0 0; }
    .section-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 1rem;
      margin-bottom: 1rem;
    }
    .lookup { justify-content: flex-start; }
    input {
      min-height: 36px;
      width: min(240px, 100%);
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
      padding: 0.45rem 0.6rem;
    }
    button {
      min-height: 36px;
      border: 1px solid #2563eb;
      border-radius: 6px;
      background: #2563eb;
      color: white;
      cursor: pointer;
      padding: 0.45rem 0.8rem;
    }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .count-pill {
      border-radius: 999px;
      background: #eff6ff;
      color: #1e40af;
      font-size: 0.78rem;
      font-weight: 700;
      padding: 0.2rem 0.6rem;
    }
    .question { color: #374151; margin: 0.85rem 0 0; }
    .token-row { justify-content: flex-start; color: #374151; font-weight: 600; margin: 0.75rem 0; }
    .debug-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 1rem;
    }
    pre {
      background: #f9fafb;
      border: 1px solid #e5e7eb;
      border-radius: 6px;
      color: #374151;
      margin: 0;
      max-height: 360px;
      overflow: auto;
      padding: 0.75rem;
      white-space: pre-wrap;
    }
    details { margin-top: 0.85rem; }
    summary { color: #374151; cursor: pointer; font-weight: 600; }
    .banner { border-radius: 8px; padding: 0.7rem 0.85rem; }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
  `]
})
export class AssistantDebugComponent {
  private api = inject(ApiService);

  runId: number | null = null;
  debug = signal<AssistantDebugExchangeListResponse | null>(null);
  loading = signal(false);
  error = signal('');

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
