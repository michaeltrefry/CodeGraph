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
    <h2>Operations</h2>

    <div class="operations-grid">
      <div class="op-card">
        <h3>Process Repos</h3>
        <p>Publish ProcessRepository messages for specific repos. One repo path per line.</p>
        <textarea [(ngModel)]="repoList" rows="4" placeholder="orders-api&#10;billing-service&#10;..."></textarea>
        <div class="options-grid">
          <label><input type="checkbox" [(ngModel)]="processShouldIndex" /> Index</label>
          <label><input type="checkbox" [(ngModel)]="processShouldAnalyze" /> Analyze</label>
          <label><input type="checkbox" [(ngModel)]="processSkipIfUpToDate" /> Skip up-to-date</label>
          <label><input type="checkbox" [(ngModel)]="processIncludeAllSource" /> Include all source</label>
        </div>
        <button (click)="runProcessRepos()" [disabled]="running()">Process</button>
      </div>

      <div class="op-card">
        <h3>Re-Index All</h3>
        <p>Publish ProcessRepository messages for all known repos.</p>
        <button (click)="confirmAndRun('indexer/repositories/reindex-all', 'Re-index ALL repositories? This may take a while.')" [disabled]="running()">Run</button>
      </div>

      <div class="op-card">
        <h3>Link &amp; Detect Clusters</h3>
        <p>Run cross-repo linking then community detection (Louvain clustering).</p>
        <button (click)="confirmAndRun('indexer/link-and-detect', 'Run cross-repo linking + community detection?')" [disabled]="running()">Run</button>
      </div>

      <div class="op-card">
        <h3>Detect Communities Only</h3>
        <p>Re-run Louvain clustering on existing cross-repo edges (no re-linking).</p>
        <button (click)="confirmAndRun('indexer/communities/detect', 'Re-run community detection?')" [disabled]="running()">Run</button>
      </div>

      <div class="op-card">
        <h3>Discover</h3>
        <p>Discover repositories from the configured source provider and index new ones.</p>
        <input type="text" [(ngModel)]="discoverFilter" placeholder="Regex filter (optional)" />
        <input type="number" [(ngModel)]="discoverLimit" min="1" placeholder="Limit (optional)" />
        <div class="options-grid">
          <label><input type="checkbox" [(ngModel)]="discoverShouldIndex" /> Index</label>
          <label><input type="checkbox" [(ngModel)]="discoverShouldAnalyze" /> Analyze</label>
          <label><input type="checkbox" [(ngModel)]="discoverSkipIfUpToDate" /> Skip up-to-date</label>
          <label><input type="checkbox" [(ngModel)]="discoverIncludeAllSource" /> Include all source</label>
        </div>
        <button (click)="runDiscover()" [disabled]="running()">Run</button>
      </div>

      <div class="op-card">
        <h3>Process Batch Analysis</h3>
        <p>Process pending batch analysis results.</p>
        <input type="text" [(ngModel)]="batchRepo" placeholder="Repo filter (optional)" />
        <button (click)="runBatchAnalysis()" [disabled]="running()">Run</button>
      </div>

      <div class="op-card">
        <h3>Regenerate MCP Docs</h3>
        <p>Regenerate MCP documentation wiki pages from current tool metadata.</p>
        <button (click)="confirmAndRun('settings/mcp/regenerate', 'Regenerate all MCP documentation pages?')" [disabled]="running()">Run</button>
      </div>
    </div>

    @if (result()) {
      <div class="result" [class.error]="!result()!.success">
        <pre>{{ result()!.message }}</pre>
      </div>
    }
  `,
  styles: [`
    .operations-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 1rem; }
    .op-card {
      padding: 1rem; border-radius: 8px; border: 1px solid #e5e7eb;
      background: white;
    }
    .op-card h3 { margin: 0 0 0.5rem; color: #111827; }
    .op-card p { font-size: 0.85rem; color: #6b7280; margin: 0 0 0.75rem; }
    .op-card input, .op-card textarea {
      width: 100%; padding: 0.4rem; margin-bottom: 0.5rem; border-radius: 4px;
      border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827; font-family: inherit; font-size: 0.85rem;
      resize: vertical;
    }
    .options-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.4rem 0.75rem;
      margin-bottom: 0.75rem;
      font-size: 0.82rem;
      color: #374151;
    }
    .options-grid label {
      display: flex;
      align-items: center;
      gap: 0.35rem;
    }
    button {
      padding: 0.4rem 1rem; border-radius: 6px; border: none;
      background: #2563eb; color: white; cursor: pointer;
    }
    button:hover { background: #1d4ed8; }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
    .result { margin-top: 1rem; padding: 1rem; border-radius: 8px; background: #f3f4f6; color: #111827; }
    .result.error { border-left: 3px solid #ef4444; }
    .result pre { margin: 0; white-space: pre-wrap; font-size: 0.85rem; }
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
