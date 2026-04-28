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
    <header class="adm-page-header">
      <div>
        <h1>Agent prompts</h1>
        <p>Edit system prompts used by analysis, reviews, and Ask.</p>
      </div>
      @if (!loading() && !error()) {
        <span class="cg-chip cg-chip-mono">{{ promptCount() }} prompts</span>
      }
    </header>

    @if (loading()) {
      <div class="adm-card cg-muted">Loading prompts...</div>
    } @else if (error()) {
      <div class="adm-banner err">{{ error() }}</div>
    } @else {
      <div class="group-list">
        @for (group of groups(); track group.category) {
          <section class="prompt-group">
            <header class="prompt-group-head">
              <div>
                <div class="adm-section-label">Workflow</div>
                <h3>{{ group.categoryDisplayName }}</h3>
              </div>
              <span class="cg-chip cg-chip-mono">{{ group.prompts.length }}</span>
            </header>

            <div class="prompt-grid">
              @for (prompt of group.prompts; track prompt.key) {
                <article class="adm-card prompt-card">
                  <header class="prompt-head">
                    <div>
                      <h4>{{ prompt.displayName }}</h4>
                      <p>{{ prompt.description }}</p>
                    </div>
                    <span class="cg-chip" [class.cg-chip-accent]="prompt.hasOverride">
                      {{ prompt.hasOverride ? 'Override' : 'Default' }}
                    </span>
                  </header>

                  <div class="meta-row">
                    <span class="cg-chip cg-chip-mono">{{ prompt.key }}</span>
                    <span>{{ formatAudit(prompt) }}</span>
                  </div>

                  <textarea
                    class="adm-textarea prompt-textarea"
                    [ngModel]="prompt.draftText"
                    (ngModelChange)="updateDraft(prompt.key, $event)"
                    rows="12"
                    spellcheck="false"
                  ></textarea>

                  @if (prompt.error) {
                    <div class="adm-banner err">{{ prompt.error }}</div>
                  }
                  @if (prompt.message) {
                    <div class="adm-banner ok">{{ prompt.message }}</div>
                  }

                  <div class="actions">
                    <button type="button" class="adm-btn primary" (click)="save(prompt)" [disabled]="prompt.saving || !isDirty(prompt)">Save</button>
                    <button type="button" class="adm-btn" (click)="reset(prompt)" [disabled]="prompt.saving || !prompt.hasOverride">Reset</button>
                    <button type="button" class="adm-btn" (click)="revert(prompt)" [disabled]="prompt.saving || !isDirty(prompt)">Revert</button>
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
    :host { display: flex; flex-direction: column; gap: 18px; }
    .prompt-group-head, .prompt-head, .actions, .meta-row {
      display: flex;
      gap: 0.75rem;
      align-items: flex-start;
    }
    .prompt-group-head, .prompt-head { justify-content: space-between; }
    h3, h4 { margin: 0; color: var(--text); }
    h4 { font-size: 1rem; }
    p, .meta-row { color: var(--muted); }
    p { margin: 0.25rem 0 0; }
    .group-list { display: flex; flex-direction: column; gap: 1.25rem; }
    .prompt-group { display: flex; flex-direction: column; gap: 0.75rem; }
    .prompt-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
      gap: 1rem;
    }
    .prompt-card { display: flex; flex-direction: column; gap: 0.85rem; min-width: 0; }
    .meta-row {
      align-items: center;
      flex-wrap: wrap;
      font-size: 0.82rem;
    }
    .prompt-textarea {
      min-height: 220px;
    }
    details {
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      overflow: hidden;
    }
    summary { cursor: pointer; color: var(--text-2); font-weight: 600; padding: 0.55rem 0.7rem; }
    pre {
      margin: 0;
      max-height: 280px;
      overflow: auto;
      border-top: 1px solid var(--border);
      color: var(--text);
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
