import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  LlmAnalysisResponse,
  LlmAssistantResponse,
  LlmProviderModelResponse,
  LlmProviderResponse,
  LlmProviderTokenActionKind,
  LlmProviderWriteRequest,
  LlmReviewResponse
} from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

type ProviderTokenMode = 'Preserve' | 'Replace' | 'Clear';
type SettingsSection = 'analysis' | 'review' | 'assistant';

interface ProviderEditor extends LlmProviderResponse {
  displayName: string;
  endpointPlaceholder: string;
  tokenMode: ProviderTokenMode;
  tokenValue: string;
  newModel: string;
  saving: boolean;
  message: string;
  error: string;
}

interface ValidationErrorPayload {
  errors?: Record<string, string[]> | { field?: string; message?: string }[];
  message?: string;
}

@Component({
  selector: 'app-admin-llm',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page-header">
      <div>
        <h2>LLM Configuration</h2>
        <p class="subtitle">Provider secrets, model catalogs, and default runtime settings for Analysis, Review, and Assistant.</p>
      </div>
      <button type="button" (click)="load()" [disabled]="loading() || savingAny()">Refresh</button>
    </div>

    <section class="notice">
      <strong>Migration status</strong>
      <span>Some settings may still be loaded from appsettings. Check API startup logs for <code>llm.config.deprecation</code> warnings to see what is pending migration.</span>
    </section>

    @if (loading()) {
      <div class="section-card muted">Loading LLM settings...</div>
    } @else if (loadError()) {
      <div class="banner error">{{ loadError() }}</div>
    } @else {
      <section class="section-group">
        <div class="section-title">
          <div>
            <div class="eyebrow">Providers</div>
            <h3>Tokens, endpoints, and model catalogs</h3>
          </div>
          <span class="count-pill">{{ providerModelCount() }} models</span>
        </div>

        <div class="provider-grid">
          @for (provider of providers; track provider.provider) {
            <article class="section-card provider-card">
              <header class="card-head">
                <div>
                  <h4>{{ provider.displayName }}</h4>
                  <p>{{ provider.provider }}</p>
                </div>
                <span class="status-pill" [class.active]="provider.hasToken">
                  {{ provider.hasToken ? 'Token stored' : 'No token' }}
                </span>
              </header>

              @if (provider.provider === 'lmstudio') {
                <div class="inline-note">LM Studio is intended for local development. Production deployments without local LLM infrastructure can leave this section unset.</div>
              }

              <div class="field-stack">
                <label>
                  <span>API token</span>
                  @if (provider.hasToken && provider.tokenMode !== 'Replace') {
                    <div class="token-row">
                      <code class="token-chip">••••••••</code>
                      <button type="button" (click)="setTokenMode(provider, 'Replace')">Replace</button>
                      <button type="button" class="danger" (click)="setTokenMode(provider, 'Clear')">Clear</button>
                    </div>
                  }
                  @if (!provider.hasToken || provider.tokenMode === 'Replace') {
                    <input type="password" [(ngModel)]="provider.tokenValue" placeholder="Paste token" autocomplete="new-password" />
                  }
                  @if (provider.tokenMode === 'Clear') {
                    <div class="pending-clear">Token will be cleared on save.</div>
                  }
                  <small>Stored encrypted at rest and never returned by the API.</small>
                </label>

                <label>
                  <span>Endpoint URL</span>
                  <input type="url" [(ngModel)]="provider.endpointUrl" [placeholder]="provider.endpointPlaceholder" />
                </label>

                @if (provider.provider === 'anthropic') {
                  <label>
                    <span>API version</span>
                    <input type="text" [(ngModel)]="provider.apiVersion" placeholder="2023-06-01" />
                  </label>
                }
              </div>

              <div class="model-editor">
                <div class="model-head">
                  <strong>Models</strong>
                  <span>{{ provider.models.length }}</span>
                </div>
                <div class="model-add">
                  <input type="text" [(ngModel)]="provider.newModel" placeholder="model-id" (keyup.enter)="addProviderModel(provider)" />
                  <button type="button" (click)="addProviderModel(provider)">Add</button>
                </div>
                @if (provider.models.length === 0) {
                  <p class="empty">No models configured.</p>
                } @else {
                  <div class="model-list">
                    @for (model of provider.models; track model; let i = $index) {
                      <div class="model-row">
                        <code>{{ model }}</code>
                        <div class="row-actions">
                          <button type="button" title="Move up" [disabled]="i === 0" (click)="moveProviderModel(provider, i, -1)">Up</button>
                          <button type="button" title="Move down" [disabled]="i === provider.models.length - 1" (click)="moveProviderModel(provider, i, 1)">Down</button>
                          <button type="button" class="danger" title="Remove model" (click)="removeProviderModel(provider, i)">Remove</button>
                        </div>
                      </div>
                    }
                  </div>
                }
              </div>

              @if (provider.error) {
                <div class="banner error">{{ provider.error }}</div>
              }
              @if (provider.message) {
                <div class="banner success">{{ provider.message }}</div>
              }

              <footer class="card-actions">
                <span class="audit">{{ audit(provider.updatedBy, provider.updatedAtUtc) }}</span>
                <button type="button" class="primary" (click)="saveProvider(provider)" [disabled]="provider.saving">Save Provider</button>
              </footer>
            </article>
          }
        </div>
      </section>

      <section class="runtime-grid">
        @if (analysis; as current) {
          <article class="section-card runtime-card">
            <header class="card-head">
              <div>
                <div class="eyebrow">Default Analysis</div>
                <h3>Repository analysis runtime</h3>
              </div>
              <span class="audit">{{ audit(current.updatedBy, current.updatedAtUtc) }}</span>
            </header>

            <div class="form-grid">
              <label>
                <span>Provider</span>
                <select [(ngModel)]="current.defaultProvider">
                  @for (provider of providers; track provider.provider) {
                    <option [value]="provider.provider">{{ provider.displayName }}</option>
                  }
                </select>
                @if (fieldError('analysis', 'defaultProvider')) {
                  <small class="field-error">{{ fieldError('analysis', 'defaultProvider') }}</small>
                }
              </label>
              <label>
                <span>Model</span>
                <select [(ngModel)]="current.defaultModel">
                  @for (model of modelOptions(current.defaultProvider, current.defaultModel); track model) {
                    <option [value]="model">{{ model }}</option>
                  }
                </select>
                @if (fieldError('analysis', 'defaultModel')) {
                  <small class="field-error">{{ fieldError('analysis', 'defaultModel') }}</small>
                }
              </label>
              <label><span>Tokens per analysis</span><input type="number" min="1" [(ngModel)]="current.maxTokensPerAnalysis" /></label>
              <label><span>Synthesis tokens</span><input type="number" min="1" [(ngModel)]="current.maxTokensPerSynthesis" /></label>
              <label><span>Max file size KB</span><input type="number" min="1" [(ngModel)]="current.maxFileSizeKb" /></label>
              <label><span>Parallel analyses</span><input type="number" min="1" [(ngModel)]="current.maxParallelAnalyses" /></label>
              <label class="wide"><span>Max source chars</span><input type="number" min="1" [(ngModel)]="current.maxSourceChars" /></label>
            </div>

            @if (sectionError.analysis) {
              <div class="banner error">{{ sectionError.analysis }}</div>
            }
            @if (sectionMessage.analysis) {
              <div class="banner success">{{ sectionMessage.analysis }}</div>
            }
            <footer class="card-actions end"><button type="button" class="primary" (click)="saveAnalysis()" [disabled]="sectionSaving.analysis">Save Analysis</button></footer>
          </article>
        }

        @if (review; as current) {
          <article class="section-card runtime-card">
            <header class="card-head">
              <div>
                <div class="eyebrow">Default Review</div>
                <h3>Code review runtime</h3>
              </div>
              <span class="audit">{{ audit(current.updatedBy, current.updatedAtUtc) }}</span>
            </header>

            <div class="form-grid">
              <label>
                <span>Provider</span>
                <select [(ngModel)]="current.defaultProvider">
                  @for (provider of providers; track provider.provider) {
                    <option [value]="provider.provider">{{ provider.displayName }}</option>
                  }
                </select>
                @if (fieldError('review', 'defaultProvider')) {
                  <small class="field-error">{{ fieldError('review', 'defaultProvider') }}</small>
                }
              </label>
              <label>
                <span>Model</span>
                <select [(ngModel)]="current.defaultModel">
                  @for (model of modelOptions(current.defaultProvider, current.defaultModel); track model) {
                    <option [value]="model">{{ model }}</option>
                  }
                </select>
                @if (fieldError('review', 'defaultModel')) {
                  <small class="field-error">{{ fieldError('review', 'defaultModel') }}</small>
                }
              </label>
              <label><span>Files to inspect</span><input type="number" min="1" [(ngModel)]="current.maxFilesToInspect" /></label>
              <label><span>Chars per file</span><input type="number" min="1" [(ngModel)]="current.maxSourceCharsPerFile" /></label>
              <label><span>Inspection passes</span><input type="number" min="1" [(ngModel)]="current.maxInspectionPasses" /></label>
              <label><span>Max findings</span><input type="number" min="1" [(ngModel)]="current.maxFindings" /></label>
            </div>

            @if (sectionError.review) {
              <div class="banner error">{{ sectionError.review }}</div>
            }
            @if (sectionMessage.review) {
              <div class="banner success">{{ sectionMessage.review }}</div>
            }
            <footer class="card-actions end"><button type="button" class="primary" (click)="saveReview()" [disabled]="sectionSaving.review">Save Review</button></footer>
          </article>
        }

        @if (assistant; as current) {
          <article class="section-card runtime-card">
            <header class="card-head">
              <div>
                <div class="eyebrow">Default Assistant</div>
                <h3>Ask and graph assistant runtime</h3>
              </div>
              <span class="audit">{{ audit(current.updatedBy, current.updatedAtUtc) }}</span>
            </header>

            <div class="form-grid">
              <label>
                <span>Provider</span>
                <select [(ngModel)]="current.defaultProvider">
                  @for (provider of providers; track provider.provider) {
                    <option [value]="provider.provider">{{ provider.displayName }}</option>
                  }
                </select>
                @if (fieldError('assistant', 'defaultProvider')) {
                  <small class="field-error">{{ fieldError('assistant', 'defaultProvider') }}</small>
                }
              </label>
              <label>
                <span>Model</span>
                <select [(ngModel)]="current.defaultModel">
                  @for (model of modelOptions(current.defaultProvider, current.defaultModel); track model) {
                    <option [value]="model">{{ model }}</option>
                  }
                </select>
                @if (fieldError('assistant', 'defaultModel')) {
                  <small class="field-error">{{ fieldError('assistant', 'defaultModel') }}</small>
                }
              </label>
              <label><span>Max tokens</span><input type="number" min="1" [(ngModel)]="current.maxTokens" /></label>
              <label><span>Max turns</span><input type="number" min="1" [(ngModel)]="current.maxTurns" /></label>
            </div>

            @if (sectionError.assistant) {
              <div class="banner error">{{ sectionError.assistant }}</div>
            }
            @if (sectionMessage.assistant) {
              <div class="banner success">{{ sectionMessage.assistant }}</div>
            }
            <footer class="card-actions end"><button type="button" class="primary" (click)="saveAssistant()" [disabled]="sectionSaving.assistant">Save Assistant</button></footer>
          </article>
        }
      </section>
    }
  `,
  styles: [`
    :host { display: block; padding: 0 0 24px; }
    .page-header, .section-title, .card-head, .card-actions, .token-row, .model-head, .model-add, .model-row {
      align-items: flex-start;
      display: flex;
      gap: 12px;
    }
    .page-header, .section-title, .card-head, .card-actions, .model-head, .model-row { justify-content: space-between; }
    .page-header { margin-bottom: 16px; }
    h2, h3, h4 { color: var(--text); margin: 0; }
    h4 { font-size: var(--fs-xl); }
    p, .subtitle, .muted, .audit, small, .empty { color: var(--muted); }
    p, .subtitle { margin: 4px 0 0; }
    .notice, .section-card {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
    }
    .notice {
      align-items: center;
      display: flex;
      gap: 10px;
      margin-bottom: 16px;
      padding: 10px 12px;
    }
    .notice span { color: var(--text-2); }
    .section-group { display: flex; flex-direction: column; gap: 12px; margin-bottom: 16px; }
    .provider-grid, .runtime-grid {
      display: grid;
      gap: 12px;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
    }
    .runtime-grid { align-items: start; }
    .section-card { padding: 16px; }
    .provider-card, .runtime-card, .field-stack { display: flex; flex-direction: column; gap: 14px; }
    .eyebrow {
      color: var(--muted);
      font-size: var(--fs-xs);
      font-weight: 700;
      letter-spacing: .06em;
      text-transform: uppercase;
    }
    .count-pill, .status-pill {
      background: var(--surface-2);
      border-radius: 999px;
      color: var(--text-2);
      flex: 0 0 auto;
      font-size: var(--fs-xs);
      font-weight: 700;
      padding: 3px 8px;
    }
    .status-pill.active { background: var(--ok-bg); color: var(--sem-green); }
    .inline-note, .pending-clear {
      background: var(--warn-bg);
      border: 1px solid color-mix(in oklab, var(--sem-amber) 40%, var(--border));
      border-radius: var(--radius);
      color: var(--text-2);
      padding: 8px 10px;
    }
    label { color: var(--text-2); display: flex; flex-direction: column; font-weight: 700; gap: 5px; }
    input, select {
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      color: var(--text);
      min-height: 36px;
      min-width: 0;
      padding: 7px 9px;
      width: 100%;
    }
    input:focus, select:focus, button:focus-visible {
      box-shadow: var(--focus-ring);
      outline: none;
    }
    button {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      color: var(--text-2);
      cursor: pointer;
      min-height: 34px;
      padding: 7px 10px;
    }
    button:hover:not(:disabled) { background: var(--surface-2); color: var(--text); }
    button.primary { background: var(--accent); border-color: var(--accent); color: var(--bg); font-weight: 700; }
    button.danger { border-color: color-mix(in oklab, var(--sem-red) 45%, var(--border)); color: var(--sem-red); }
    button:disabled { cursor: not-allowed; opacity: .55; }
    .token-row { align-items: center; flex-wrap: wrap; }
    .token-chip, code {
      background: var(--surface-3);
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      color: var(--text-2);
      overflow-wrap: anywhere;
      padding: 3px 6px;
    }
    .model-editor { display: flex; flex-direction: column; gap: 8px; }
    .model-add { align-items: center; }
    .model-add input { flex: 1; }
    .model-list { border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }
    .model-row { align-items: center; border-bottom: 1px solid var(--hairline); padding: 8px; }
    .model-row:last-child { border-bottom: 0; }
    .row-actions { display: flex; flex: 0 0 auto; gap: 4px; }
    .row-actions button { font-size: var(--fs-xs); min-height: 28px; padding: 4px 7px; }
    .form-grid { display: grid; gap: 12px; grid-template-columns: repeat(2, minmax(160px, 1fr)); }
    .form-grid .wide { grid-column: 1 / -1; }
    .field-error { color: var(--sem-red); font-weight: 600; }
    .banner { border-radius: var(--radius); padding: 9px 11px; }
    .banner.error { background: var(--err-bg); border: 1px solid color-mix(in oklab, var(--sem-red) 45%, var(--border)); color: var(--sem-red); }
    .banner.success { background: var(--ok-bg); border: 1px solid color-mix(in oklab, var(--sem-green) 45%, var(--border)); color: var(--sem-green); }
    .end { justify-content: flex-end; }
    @media (max-width: 760px) {
      .page-header, .section-title, .card-head, .card-actions, .notice { flex-direction: column; }
      .provider-grid, .runtime-grid, .form-grid { grid-template-columns: 1fr; }
      .model-row { align-items: stretch; flex-direction: column; }
      .row-actions { flex-wrap: wrap; }
    }
  `]
})
export class AdminLlmComponent implements OnInit {
  private api = inject(ApiService);

  providers: ProviderEditor[] = [];
  catalog: LlmProviderModelResponse[] = [];
  analysis: LlmAnalysisResponse | null = null;
  review: LlmReviewResponse | null = null;
  assistant: LlmAssistantResponse | null = null;

  loading = signal(false);
  loadError = signal('');

  sectionSaving: Record<SettingsSection, boolean> = { analysis: false, review: false, assistant: false };
  sectionMessage: Record<SettingsSection, string> = { analysis: '', review: '', assistant: '' };
  sectionError: Record<SettingsSection, string> = { analysis: '', review: '', assistant: '' };
  fieldErrors: Record<SettingsSection, Record<string, string>> = { analysis: {}, review: {}, assistant: {} };

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set('');
    try {
      const [providers, catalog, analysis, review, assistant] = await Promise.all([
        firstValueFrom(this.api.listLlmProviders()),
        firstValueFrom(this.api.listLlmProviderModels()),
        firstValueFrom(this.api.getLlmAnalysis()),
        firstValueFrom(this.api.getLlmReview()),
        firstValueFrom(this.api.getLlmAssistant())
      ]);
      this.providers = providers.map(provider => this.toProviderEditor(provider));
      this.catalog = catalog;
      this.analysis = analysis;
      this.review = review;
      this.assistant = assistant;
      this.clearSectionMessages();
    } catch (err) {
      this.loadError.set(extractAdminError(err, 'Failed to load LLM settings'));
    } finally {
      this.loading.set(false);
    }
  }

  savingAny(): boolean {
    return this.providers.some(provider => provider.saving) ||
      this.sectionSaving.analysis ||
      this.sectionSaving.review ||
      this.sectionSaving.assistant;
  }

  providerModelCount(): number {
    return this.providers.reduce((total, provider) => total + provider.models.length, 0);
  }

  setTokenMode(provider: ProviderEditor, mode: ProviderTokenMode): void {
    provider.tokenMode = mode;
    provider.tokenValue = '';
    provider.message = '';
    provider.error = '';
  }

  addProviderModel(provider: ProviderEditor): void {
    const model = provider.newModel.trim();
    if (!model) return;
    if (!provider.models.includes(model)) provider.models = [...provider.models, model];
    provider.newModel = '';
  }

  removeProviderModel(provider: ProviderEditor, index: number): void {
    provider.models = provider.models.filter((_, i) => i !== index);
  }

  moveProviderModel(provider: ProviderEditor, index: number, direction: -1 | 1): void {
    const target = index + direction;
    if (target < 0 || target >= provider.models.length) return;
    const models = [...provider.models];
    [models[index], models[target]] = [models[target], models[index]];
    provider.models = models;
  }

  async saveProvider(provider: ProviderEditor): Promise<void> {
    provider.error = '';
    provider.message = '';
    if (provider.tokenMode === 'Replace' && !provider.tokenValue.trim()) {
      provider.error = 'Token is required when replacing.';
      return;
    }

    provider.saving = true;
    try {
      const request: LlmProviderWriteRequest = {
        endpointUrl: this.trimToNull(provider.endpointUrl),
        apiVersion: provider.provider === 'anthropic' ? this.trimToNull(provider.apiVersion) : null,
        models: provider.models.map(model => model.trim()).filter(Boolean),
        token: this.tokenRequest(provider)
      };
      const updated = await firstValueFrom(this.api.updateLlmProvider(provider.provider, request));
      const index = this.providers.findIndex(item => item.provider === provider.provider);
      this.providers[index] = { ...this.toProviderEditor(updated), message: 'Provider saved.' };
      this.catalog = await firstValueFrom(this.api.listLlmProviderModels());
    } catch (err) {
      provider.error = extractAdminError(err, 'Failed to save provider');
    } finally {
      provider.saving = false;
    }
  }

  modelOptions(provider: string, selected: string): string[] {
    const models = this.catalog
      .filter(item => item.provider === provider)
      .map(item => item.model);
    if (selected && !models.includes(selected)) return [selected, ...models];
    return models;
  }

  fieldError(section: SettingsSection, field: 'defaultProvider' | 'defaultModel'): string {
    return this.fieldErrors[section][field] ?? '';
  }

  async saveAnalysis(): Promise<void> {
    if (!this.analysis) return;
    await this.saveSettings('analysis', () => this.api.updateLlmAnalysis(this.analysis!));
  }

  async saveReview(): Promise<void> {
    if (!this.review) return;
    await this.saveSettings('review', () => this.api.updateLlmReview(this.review!));
  }

  async saveAssistant(): Promise<void> {
    if (!this.assistant) return;
    await this.saveSettings('assistant', () => this.api.updateLlmAssistant(this.assistant!));
  }

  audit(updatedBy?: string, updatedAtUtc?: string): string {
    if (!updatedAtUtc) return 'Using fallback/defaults';
    return updatedBy ? `Updated by ${updatedBy}` : 'Updated';
  }

  private async saveSettings<T extends LlmAnalysisResponse | LlmReviewResponse | LlmAssistantResponse>(
    section: SettingsSection,
    action: () => Observable<T>
  ): Promise<void> {
    this.sectionSaving[section] = true;
    this.sectionError[section] = '';
    this.sectionMessage[section] = '';
    this.fieldErrors[section] = {};
    try {
      const updated = await firstValueFrom(action());
      if (section === 'analysis') this.analysis = updated as LlmAnalysisResponse;
      if (section === 'review') this.review = updated as LlmReviewResponse;
      if (section === 'assistant') this.assistant = updated as LlmAssistantResponse;
      this.sectionMessage[section] = `${this.sectionLabel(section)} saved.`;
    } catch (err) {
      const validationErrors = this.extractValidationErrors(err);
      if (Object.keys(validationErrors).length > 0) {
        this.fieldErrors[section] = validationErrors;
        this.sectionError[section] = 'Provider and model selection could not be saved.';
      } else {
        this.sectionError[section] = extractAdminError(err, `Failed to save ${this.sectionLabel(section).toLowerCase()} settings`);
      }
    } finally {
      this.sectionSaving[section] = false;
    }
  }

  private toProviderEditor(provider: LlmProviderResponse): ProviderEditor {
    return {
      ...provider,
      displayName: this.providerName(provider.provider),
      endpointPlaceholder: this.endpointPlaceholder(provider.provider),
      tokenMode: provider.hasToken ? 'Preserve' : 'Replace',
      tokenValue: '',
      newModel: '',
      saving: false,
      message: '',
      error: ''
    };
  }

  private tokenRequest(provider: ProviderEditor): LlmProviderWriteRequest['token'] {
    const action = provider.tokenMode as LlmProviderTokenActionKind;
    if (action === 'Replace') return { action, value: provider.tokenValue.trim() };
    return { action };
  }

  private providerName(provider: string): string {
    if (provider === 'anthropic') return 'Anthropic';
    if (provider === 'openai') return 'OpenAI compatible';
    if (provider === 'lmstudio') return 'LM Studio';
    return provider;
  }

  private endpointPlaceholder(provider: string): string {
    if (provider === 'anthropic') return 'https://api.anthropic.com';
    if (provider === 'openai') return 'https://api.openai.com/v1';
    return 'http://localhost:1234/v1';
  }

  private sectionLabel(section: SettingsSection): string {
    if (section === 'analysis') return 'Analysis';
    if (section === 'review') return 'Review';
    return 'Assistant';
  }

  private trimToNull(value?: string | null): string | null {
    const trimmed = value?.trim() ?? '';
    return trimmed ? trimmed : null;
  }

  private clearSectionMessages(): void {
    this.sectionMessage = { analysis: '', review: '', assistant: '' };
    this.sectionError = { analysis: '', review: '', assistant: '' };
    this.fieldErrors = { analysis: {}, review: {}, assistant: {} };
  }

  private extractValidationErrors(error: unknown): Record<string, string> {
    const payload = (error as { error?: ValidationErrorPayload | null } | null)?.error;
    const result: Record<string, string> = {};
    const errors = payload?.errors;
    if (Array.isArray(errors)) {
      for (const item of errors) {
        if (item.field && item.message) result[this.normalizeField(item.field)] = item.message;
      }
      return result;
    }
    if (errors && typeof errors === 'object') {
      for (const [field, messages] of Object.entries(errors)) {
        if (Array.isArray(messages) && messages.length > 0) {
          result[this.normalizeField(field)] = messages[0];
        }
      }
    }
    return result;
  }

  private normalizeField(field: string): string {
    if (field === 'default_provider') return 'defaultProvider';
    if (field === 'default_model') return 'defaultModel';
    return field;
  }
}
