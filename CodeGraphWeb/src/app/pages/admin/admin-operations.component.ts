import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const API = environment.apiUrl;

interface OperationResult {
  success: boolean;
  message: string;
}

@Component({
  selector: 'app-admin-operations',
  standalone: true,
  imports: [FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Operations</h1>
        <p>Trigger repository discovery, indexing, graph linking, MCP documentation, and cleanup jobs.</p>
      </div>
      @if (running()) {
        <span class="cg-chip cg-chip-accent cg-chip-dot">running</span>
      }
    </header>

    <div class="operations-grid">
      <article class="adm-card op-card op-card-wide">
        <header class="op-card-head">
          <div>
            <h2>Process Repos</h2>
            <p>Publish ProcessRepository messages for specific repos. One repo path per line.</p>
          </div>
          <span class="cg-chip">targeted</span>
        </header>

        <label class="adm-field">
          <span class="adm-field-label">Repository paths</span>
          <textarea class="adm-textarea op-textarea" [(ngModel)]="repoList" rows="4" placeholder="orders-api&#10;billing-service&#10;..."></textarea>
        </label>

        <div class="options-grid">
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="processShouldIndex" /><span>Index</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="processShouldAnalyze" /><span>Analyze</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="processSkipIfUpToDate" /><span>Skip up-to-date</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="processIncludeAllSource" /><span>Include all source</span></label>
        </div>

        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="runProcessRepos()" [disabled]="running()">Process</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Re-Index All</h2>
            <p>Publish ProcessRepository messages for all known repos.</p>
          </div>
          <span class="cg-chip cg-chip-warn">bulk</span>
        </header>
        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="confirmAndRun('indexer/repositories/reindex-all', 'Re-index ALL repositories? This may take a while.')" [disabled]="running()">Run</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Link &amp; Detect Clusters</h2>
            <p>Run cross-repo linking then community detection.</p>
          </div>
          <span class="cg-chip cg-chip-accent">graph</span>
        </header>
        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="confirmAndRun('indexer/link-and-detect', 'Run cross-repo linking + community detection?')" [disabled]="running()">Run</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Detect Communities Only</h2>
            <p>Re-run clustering on existing cross-repo edges without re-linking.</p>
          </div>
          <span class="cg-chip cg-chip-accent">clusters</span>
        </header>
        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="confirmAndRun('indexer/communities/detect', 'Re-run community detection?')" [disabled]="running()">Run</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Discover</h2>
            <p>Discover repositories from the configured source provider and index new ones.</p>
          </div>
          <span class="cg-chip">source</span>
        </header>

        <div class="op-form-grid">
          <label class="adm-field">
            <span class="adm-field-label">Regex filter</span>
            <input class="adm-input" type="text" [(ngModel)]="discoverFilter" placeholder="Optional" />
          </label>
          <label class="adm-field narrow">
            <span class="adm-field-label">Limit</span>
            <input class="adm-input" type="number" [(ngModel)]="discoverLimit" min="1" placeholder="Optional" />
          </label>
        </div>

        <div class="options-grid">
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="discoverShouldIndex" /><span>Index</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="discoverShouldAnalyze" /><span>Analyze</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="discoverSkipIfUpToDate" /><span>Skip up-to-date</span></label>
          <label class="adm-checkbox"><input type="checkbox" [(ngModel)]="discoverIncludeAllSource" /><span>Include all source</span></label>
        </div>

        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="runDiscover()" [disabled]="running()">Run</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Process Batch Analysis</h2>
            <p>Process pending batch analysis results.</p>
          </div>
          <span class="cg-chip">analysis</span>
        </header>
        <label class="adm-field">
          <span class="adm-field-label">Repository filter</span>
          <input class="adm-input" type="text" [(ngModel)]="batchRepo" placeholder="Optional" />
        </label>
        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="runBatchAnalysis()" [disabled]="running()">Run</button>
        </div>
      </article>

      <article class="adm-card op-card">
        <header class="op-card-head">
          <div>
            <h2>Regenerate MCP Docs</h2>
            <p>Regenerate MCP documentation wiki pages from current tool metadata.</p>
          </div>
          <span class="cg-chip cg-chip-accent">wiki</span>
        </header>
        <div class="op-actions">
          <button class="adm-btn primary" type="button" (click)="confirmAndRun('settings/mcp/regenerate', 'Regenerate all MCP documentation pages?')" [disabled]="running()">Run</button>
        </div>
      </article>
    </div>

    @if (result()) {
      <section class="adm-card result-card" [class.error]="!result()!.success">
        <header class="adm-card-head">
          <span class="adm-section-label">Last result</span>
          <span class="cg-chip cg-chip-dot" [class.cg-chip-ok]="result()!.success" [class.cg-chip-err]="!result()!.success">
            {{ result()!.success ? 'success' : 'failed' }}
          </span>
        </header>
        <pre>{{ result()!.message }}</pre>
      </section>
    }
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }

    .operations-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 14px;
    }

    .op-card {
      min-height: 190px;
    }

    .op-card-wide {
      grid-column: span 2;
    }

    .op-card-head {
      display: flex;
      gap: 12px;
      justify-content: space-between;
      align-items: flex-start;
    }

    .op-card h2 {
      color: var(--text);
      font-size: var(--fs-h3);
      margin: 0;
    }

    .op-card p {
      color: var(--muted);
      font-size: var(--fs-sm);
      line-height: 1.5;
      margin: 4px 0 0;
    }

    .op-textarea {
      min-height: 112px;
      resize: vertical;
    }

    .op-form-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(110px, 140px);
      gap: 10px;
    }

    .options-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 4px 8px;
    }

    .op-actions {
      display: flex;
      justify-content: flex-end;
      margin-top: auto;
    }

    .result-card {
      gap: 12px;
    }

    .result-card.error {
      border-color: color-mix(in oklab, var(--sem-red) 35%, var(--border));
    }

    .result-card pre {
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      color: var(--text);
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.5;
      margin: 0;
      overflow-x: auto;
      padding: 12px;
      white-space: pre-wrap;
    }

    @media (max-width: 760px) {
      .op-card-wide {
        grid-column: auto;
      }

      .op-form-grid,
      .options-grid {
        grid-template-columns: 1fr;
      }
    }
  `]
})
export class AdminOperationsComponent {
  private http = inject(HttpClient);

  running = signal(false);
  result = signal<OperationResult | null>(null);
  discoverFilter = '';
  discoverLimit: number | null = null;
  batchRepo = '';
  repoList = '';
  processShouldIndex = true;
  processShouldAnalyze = true;
  processSkipIfUpToDate = true;
  processIncludeAllSource = false;
  discoverShouldIndex = true;
  discoverShouldAnalyze = true;
  discoverSkipIfUpToDate = true;
  discoverIncludeAllSource = false;

  async confirmAndRun(endpoint: string, message: string): Promise<void> {
    if (!confirm(message)) return;
    await this.runOp(endpoint);
  }

  async runOp(endpoint: string): Promise<void> {
    this.running.set(true);
    this.result.set(null);
    try {
      const res = await firstValueFrom(this.http.post(`${API}/${endpoint}`, {}));
      this.result.set({ success: true, message: JSON.stringify(res, null, 2) || 'OK' });
    } catch (err: any) {
      this.result.set({ success: false, message: err.error?.message || err.error || err.message || 'Failed' });
    } finally {
      this.running.set(false);
    }
  }

  async runProcessRepos(): Promise<void> {
    const lines = this.repoList.split('\n').map(l => l.trim()).filter(l => l.length > 0);
    if (lines.length === 0) {
      this.result.set({ success: false, message: 'Enter at least one repo path.' });
      return;
    }

    this.running.set(true);
    this.result.set(null);
    try {
      const body = {
        repos: lines,
        shouldIndex: this.processShouldIndex,
        shouldAnalyze: this.processShouldAnalyze,
        skipIfUpToDate: this.processSkipIfUpToDate,
        includeAllSource: this.processIncludeAllSource
      };
      const res = await firstValueFrom(this.http.post(`${API}/indexer/repositories/process`, body));
      this.result.set({ success: true, message: JSON.stringify(res, null, 2) });
    } catch (err: any) {
      this.result.set({ success: false, message: err.error?.message || err.error || err.message || 'Failed' });
    } finally {
      this.running.set(false);
    }
  }

  async runDiscover(): Promise<void> {
    this.running.set(true);
    this.result.set(null);
    try {
      const body: Record<string, string | number | boolean> = {
        shouldIndex: this.discoverShouldIndex,
        shouldAnalyze: this.discoverShouldAnalyze,
        skipIfUpToDate: this.discoverSkipIfUpToDate,
        includeAllSource: this.discoverIncludeAllSource
      };
      if (this.discoverFilter.trim()) body['namePattern'] = this.discoverFilter.trim();
      if (this.discoverLimit && this.discoverLimit > 0) body['limit'] = this.discoverLimit;
      const res = await firstValueFrom(this.http.post(`${API}/indexer/repositories/discover`, body));
      this.result.set({ success: true, message: JSON.stringify(res, null, 2) });
    } catch (err: any) {
      this.result.set({ success: false, message: err.error?.message || err.error || err.message || 'Failed' });
    } finally {
      this.running.set(false);
    }
  }

  async runBatchAnalysis(): Promise<void> {
    this.running.set(true);
    this.result.set(null);
    try {
      let url = `${API}/indexer/batch-analysis/process`;
      if (this.batchRepo) url += `?repo=${encodeURIComponent(this.batchRepo)}`;
      const res = await firstValueFrom(this.http.post(url, {}));
      this.result.set({ success: true, message: JSON.stringify(res, null, 2) || 'OK' });
    } catch (err: any) {
      this.result.set({ success: false, message: err.error?.message || err.error || err.message || 'Failed' });
    } finally {
      this.running.set(false);
    }
  }
}
