import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AgentPromptGroupResponse, AgentPromptResponse } from '../../core/models';
import { extractAdminError } from './admin-resource.helpers';

interface PromptEditorState extends AgentPromptResponse {
  draftText: string;
  saving: boolean;
  error: string;
  message: string;
}

interface PromptGroupState {
  category: string;
  categoryDisplayName: string;
  prompts: PromptEditorState[];
}

@Component({
  selector: 'app-admin-prompts',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page-header">
      <div>
        <h2>Agent Prompts</h2>
        <p class="subtitle">Edit system prompts used by analysis, reviews, and Ask.</p>
      </div>
      @if (!loading() && !error()) {
        <span class="count-pill">{{ promptCount() }} prompts</span>
      }
    </div>

    @if (loading()) {
      <div class="section-card muted">Loading prompts...</div>
    } @else if (error()) {
      <div class="banner error">{{ error() }}</div>
    } @else {
      <div class="group-list">
        @for (group of groups(); track group.category) {
          <section class="prompt-group">
            <div class="section-header">
              <div>
                <div class="eyebrow">Workflow</div>
                <h3>{{ group.categoryDisplayName }}</h3>
              </div>
              <span class="count-pill">{{ group.prompts.length }}</span>
            </div>

            <div class="prompt-grid">
              @for (prompt of group.prompts; track prompt.key) {
                <article class="section-card prompt-card">
                  <header class="prompt-head">
                    <div>
                      <h4>{{ prompt.displayName }}</h4>
                      <p>{{ prompt.description }}</p>
                    </div>
                    <span class="status-pill" [class.active]="prompt.hasOverride">
                      {{ prompt.hasOverride ? 'Override' : 'Default' }}
                    </span>
                  </header>

                  <div class="meta-row">
                    <code>{{ prompt.key }}</code>
                    <span>{{ formatAudit(prompt) }}</span>
                  </div>

                  <textarea
                    [ngModel]="prompt.draftText"
                    (ngModelChange)="updateDraft(prompt.key, $event)"
                    rows="12"
                    spellcheck="false"
                  ></textarea>

                  @if (prompt.error) {
                    <div class="banner error">{{ prompt.error }}</div>
                  }
                  @if (prompt.message) {
                    <div class="banner success">{{ prompt.message }}</div>
                  }

                  <div class="actions">
                    <button type="button" class="primary" (click)="save(prompt)" [disabled]="prompt.saving || !isDirty(prompt)">Save</button>
                    <button type="button" (click)="reset(prompt)" [disabled]="prompt.saving || !prompt.hasOverride">Reset</button>
                    <button type="button" (click)="revert(prompt)" [disabled]="prompt.saving || !isDirty(prompt)">Revert</button>
                  </div>

                  <details>
                    <summary>Code default</summary>
                    <pre>{{ prompt.defaultText }}</pre>
                  </details>
                </article>
              }
            </div>
          </section>
        }
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .page-header, .section-header, .prompt-head, .actions, .meta-row {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
    }
    .page-header, .section-header, .prompt-head { justify-content: space-between; }
    .page-header { margin-bottom: 1rem; }
    h2, h3, h4 { margin: 0; color: #111827; }
    h4 { font-size: 1rem; }
    p, .subtitle, .muted, .meta-row { color: #6b7280; }
    p { margin: 0.25rem 0 0; }
    .eyebrow {
      color: #6b7280;
      font-size: 0.76rem;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .group-list { display: flex; flex-direction: column; gap: 1.25rem; }
    .prompt-group { display: flex; flex-direction: column; gap: 0.75rem; }
    .prompt-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
      gap: 1rem;
    }
    .section-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 1rem;
    }
    .prompt-card { display: flex; flex-direction: column; gap: 0.85rem; min-width: 0; }
    .count-pill, .status-pill {
      border-radius: 999px;
      background: #f3f4f6;
      color: #374151;
      flex: 0 0 auto;
      font-size: 0.78rem;
      font-weight: 700;
      padding: 0.2rem 0.6rem;
    }
    .status-pill.active { background: #eff6ff; color: #1e40af; }
    .meta-row {
      align-items: center;
      flex-wrap: wrap;
      font-size: 0.82rem;
    }
    textarea {
      width: 100%;
      min-height: 220px;
      padding: 0.65rem;
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      font-size: 0.82rem;
      line-height: 1.55;
      resize: vertical;
    }
    button {
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: white;
      color: #374151;
      cursor: pointer;
      padding: 0.45rem 0.8rem;
    }
    button.primary { background: #2563eb; border-color: #2563eb; color: white; }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .banner { border-radius: 8px; padding: 0.7rem 0.85rem; }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
    .banner.success { background: #f0fdf4; border: 1px solid #bbf7d0; color: #166534; }
    details {
      border: 1px solid #e5e7eb;
      border-radius: 6px;
      background: #f9fafb;
      overflow: hidden;
    }
    summary { cursor: pointer; color: #374151; font-weight: 600; padding: 0.55rem 0.7rem; }
    pre {
      margin: 0;
      max-height: 280px;
      overflow: auto;
      border-top: 1px solid #e5e7eb;
      color: #374151;
      padding: 0.75rem;
      white-space: pre-wrap;
    }
    code, pre { overflow-wrap: anywhere; }
  `]
})
export class AdminPromptsComponent implements OnInit {
  private api = inject(ApiService);

  groups = signal<PromptGroupState[]>([]);
  loading = signal(false);
  error = signal('');

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const groups = await firstValueFrom(this.api.getAdminPrompts());
      this.groups.set(groups.map(group => this.mapGroup(group)));
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to load prompts'));
    } finally {
      this.loading.set(false);
    }
  }

  promptCount(): number {
    return this.groups().reduce((total, group) => total + group.prompts.length, 0);
  }

  updateDraft(key: string, value: string): void {
    this.groups.update(groups => groups.map(group => ({
      ...group,
      prompts: group.prompts.map(prompt =>
        prompt.key === key ? { ...prompt, draftText: value, error: '', message: '' } : prompt)
    })));
  }

  isDirty(prompt: PromptEditorState): boolean {
    return prompt.draftText !== prompt.effectiveText;
  }

  async save(prompt: PromptEditorState): Promise<void> {
    this.setPromptState(prompt.key, { saving: true, error: '', message: '' });
    try {
      const updated = await firstValueFrom(this.api.updateAdminPrompt(prompt.key, prompt.draftText));
      this.replacePrompt(updated, 'Prompt saved.');
    } catch (err) {
      this.setPromptState(prompt.key, {
        saving: false,
        error: extractAdminError(err, 'Failed to save prompt')
      });
    }
  }

  async reset(prompt: PromptEditorState): Promise<void> {
    if (!confirm(`Reset '${prompt.displayName}' to the code default?`)) return;

    this.setPromptState(prompt.key, { saving: true, error: '', message: '' });
    try {
      await firstValueFrom(this.api.resetAdminPrompt(prompt.key));
      this.replacePrompt({
        ...prompt,
        effectiveText: prompt.defaultText,
        hasOverride: false,
        updatedBy: undefined,
        updatedAt: undefined
      }, 'Override removed. Using code default.');
    } catch (err) {
      this.setPromptState(prompt.key, {
        saving: false,
        error: extractAdminError(err, 'Failed to reset prompt')
      });
    }
  }

  revert(prompt: PromptEditorState): void {
    this.setPromptState(prompt.key, {
      draftText: prompt.effectiveText,
      error: '',
      message: ''
    });
  }

  formatAudit(prompt: PromptEditorState): string {
    if (!prompt.hasOverride) return 'Using code default';
    const by = prompt.updatedBy ? ` by ${prompt.updatedBy}` : '';
    return prompt.updatedAt ? `Updated${by} ${new Date(prompt.updatedAt).toLocaleString()}` : `Updated${by}`;
  }

  private mapGroup(group: AgentPromptGroupResponse): PromptGroupState {
    return {
      category: group.category,
      categoryDisplayName: group.categoryDisplayName,
      prompts: group.prompts.map(prompt => this.mapPrompt(prompt))
    };
  }

  private mapPrompt(prompt: AgentPromptResponse, message = ''): PromptEditorState {
    return {
      ...prompt,
      draftText: prompt.effectiveText,
      saving: false,
      error: '',
      message
    };
  }

  private replacePrompt(prompt: AgentPromptResponse, message: string): void {
    this.groups.update(groups => groups.map(group => ({
      ...group,
      prompts: group.prompts.map(existing =>
        existing.key === prompt.key ? this.mapPrompt(prompt, message) : existing)
    })));
  }

  private setPromptState(key: string, patch: Partial<PromptEditorState>): void {
    this.groups.update(groups => groups.map(group => ({
      ...group,
      prompts: group.prompts.map(prompt => prompt.key === key ? { ...prompt, ...patch } : prompt)
    })));
  }
}
