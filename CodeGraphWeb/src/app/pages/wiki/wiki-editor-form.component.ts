import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-wiki-editor-form',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="wef-form">
      <label class="wef-field">
        <span class="wef-label">Title</span>
        <input
          type="text"
          [ngModel]="title"
          (ngModelChange)="titleChange.emit($event)"
          placeholder="Title"
          class="wef-input"
        />
      </label>

      <label class="wef-field">
        <span class="wef-label">Content <span class="cg-faint cg-xsmall">(Markdown)</span></span>
        <textarea
          [ngModel]="content"
          (ngModelChange)="contentChange.emit($event)"
          rows="18"
          class="wef-textarea"
          spellcheck="false"
        ></textarea>
      </label>

      @if (hasRawContent) {
        <label class="wef-field">
          <span class="wef-label">Raw content <span class="cg-faint cg-xsmall">(original file)</span></span>
          <textarea
            [ngModel]="rawContent"
            (ngModelChange)="rawContentChange.emit($event)"
            rows="18"
            class="wef-textarea raw"
            spellcheck="false"
            placeholder="Paste the raw file content here (e.g., skill definition, agent config)"
          ></textarea>
        </label>
      }

      <label class="wef-field narrow">
        <span class="wef-label">Author</span>
        <input
          type="text"
          [ngModel]="author"
          (ngModelChange)="authorChange.emit($event)"
          [disabled]="authorReadonly"
          class="wef-input"
          [class.disabled]="authorReadonly"
          placeholder="Your name"
        />
      </label>

      @if (saveError) {
        <div class="wef-error">{{ saveError }}</div>
      }

      <div class="wef-actions">
        <button class="wef-btn" type="button" (click)="cancel.emit()">Cancel</button>
        <button class="wef-btn primary" type="button" (click)="save.emit()">Save</button>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .wef-form {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .wef-field {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .wef-field.narrow { max-width: 320px; }

    .wef-label {
      font-size: var(--fs-xs);
      font-weight: 600;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: var(--muted);
    }

    .wef-input, .wef-textarea {
      width: 100%;
      padding: 10px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
      transition: border-color var(--transition), background var(--transition), box-shadow var(--transition);
    }
    .wef-input {
      height: 38px;
      font-size: var(--fs-lg);
    }
    .wef-input.disabled {
      background: var(--surface);
      color: var(--muted);
      cursor: not-allowed;
    }
    .wef-textarea {
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.55;
      resize: vertical;
      min-height: 180px;
    }
    .wef-textarea.raw {
      border-color: var(--accent-dim);
      background: color-mix(in oklab, var(--accent-weak) 50%, var(--surface-2));
    }

    .wef-input:focus, .wef-textarea:focus {
      outline: none;
      border-color: var(--accent-dim);
      background: var(--surface);
      box-shadow: 0 0 0 2px var(--accent-weak);
    }

    .wef-error {
      padding: 10px 14px;
      border-radius: var(--radius);
      background: var(--err-bg);
      border: 1px solid color-mix(in oklab, var(--sem-red) 30%, var(--border));
      color: var(--sem-red);
      font-size: var(--fs-sm);
    }

    .wef-actions {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      flex-wrap: wrap;
    }

    .wef-btn {
      appearance: none;
      min-height: 34px;
      padding: 8px 16px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface);
      color: var(--text-2);
      font: inherit;
      font-size: var(--fs-sm);
      font-weight: 500;
      cursor: pointer;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
    }
    .wef-btn:hover {
      background: var(--surface-2);
      color: var(--text);
      border-color: var(--border-2);
    }
    .wef-btn.primary {
      background: var(--accent);
      color: white;
      border-color: var(--accent);
    }
    .wef-btn.primary:hover {
      background: color-mix(in oklab, var(--accent) 85%, black);
    }
  `]
})
export class WikiEditorFormComponent {
  @Input() title = '';
  @Input() content = '';
  @Input() rawContent = '';
  @Input() hasRawContent = false;
  @Input() author = '';
  @Input() authorReadonly = false;
  @Input() saveError = '';

  @Output() titleChange = new EventEmitter<string>();
  @Output() contentChange = new EventEmitter<string>();
  @Output() rawContentChange = new EventEmitter<string>();
  @Output() authorChange = new EventEmitter<string>();
  @Output() cancel = new EventEmitter<void>();
  @Output() save = new EventEmitter<void>();
}
