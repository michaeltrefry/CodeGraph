import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { SECTION_ROOT_SLUG } from './wiki.constants';

@Component({
  selector: 'app-wiki-page-new',
  standalone: true,
  imports: [FormsModule],
  template: `
    <header class="wkn-header">
      <h1 class="wkn-title">{{ isRootPage() ? 'New section page' : 'New page' }}</h1>
    </header>

    <div class="wkn-form">
      <label class="wkn-field">
        <span class="wkn-label">Title</span>
        <input type="text" [(ngModel)]="title" placeholder="Page title" class="wkn-input" />
      </label>

      <label class="wkn-field">
        <span class="wkn-label">Content <span class="cg-faint cg-xsmall">(Markdown)</span></span>
        <textarea [(ngModel)]="content" rows="18" placeholder="Page content (markdown)" class="wkn-textarea" spellcheck="false"></textarea>
      </label>

      @if (hasRawContent()) {
        <label class="wkn-field">
          <span class="wkn-label">Raw content <span class="cg-faint cg-xsmall">(original file)</span></span>
          <textarea
            [(ngModel)]="rawContent"
            rows="18"
            class="wkn-textarea raw"
            placeholder="Paste the raw file content here (e.g., skill definition, agent config)"
            spellcheck="false"
          ></textarea>
        </label>
      }

      <label class="wkn-field narrow">
        <span class="wkn-label">Author</span>
        <input
          type="text"
          [(ngModel)]="author"
          [disabled]="!!currentUsername()"
          class="wkn-input"
          [class.disabled]="!!currentUsername()"
          placeholder="Your name"
        />
      </label>

      @if (error()) {
        <div class="wkn-error">{{ error() }}</div>
      }

      <div class="wkn-actions">
        <button class="wkn-btn" type="button" (click)="cancel()">Cancel</button>
        <button class="wkn-btn primary" type="button" (click)="create()">Create</button>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      width: min(100%, 860px);
      max-width: 860px;
      margin: 0 auto;
    }

    .wkn-header { margin-bottom: 24px; }
    .wkn-title {
      margin: 0;
      font-size: 28px;
      font-weight: 600;
      color: var(--text);
    }

    .wkn-form {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .wkn-field {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .wkn-field.narrow { max-width: 320px; }

    .wkn-label {
      font-size: var(--fs-xs);
      font-weight: 600;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: var(--muted);
    }

    .wkn-input, .wkn-textarea {
      width: 100%;
      padding: 10px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
      transition: border-color var(--transition), background var(--transition), box-shadow var(--transition);
    }
    .wkn-input {
      height: 38px;
      font-size: var(--fs-lg);
    }
    .wkn-input.disabled {
      background: var(--surface);
      color: var(--muted);
      cursor: not-allowed;
    }
    .wkn-textarea {
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.55;
      resize: vertical;
      min-height: 180px;
    }
    .wkn-textarea.raw {
      border-color: var(--accent-dim);
      background: color-mix(in oklab, var(--accent-weak) 50%, var(--surface-2));
    }

    .wkn-input:focus, .wkn-textarea:focus {
      outline: none;
      border-color: var(--accent-dim);
      background: var(--surface);
      box-shadow: 0 0 0 2px var(--accent-weak);
    }

    .wkn-error {
      padding: 10px 14px;
      border-radius: var(--radius);
      background: var(--err-bg);
      border: 1px solid color-mix(in oklab, var(--sem-red) 30%, var(--border));
      color: var(--sem-red);
      font-size: var(--fs-sm);
    }

    .wkn-actions {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      flex-wrap: wrap;
    }

    .wkn-btn {
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
    .wkn-btn:hover {
      background: var(--surface-2);
      color: var(--text);
      border-color: var(--border-2);
    }
    .wkn-btn.primary {
      background: var(--accent);
      color: white;
      border-color: var(--accent);
    }
    .wkn-btn.primary:hover {
      background: color-mix(in oklab, var(--accent) 85%, black);
    }
  `]
})
export class WikiPageNewComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(AuthService);

  title = '';
  content = '';
  rawContent = '';
  author = '';
  error = signal('');
  hasRawContent = signal(false);
  isRootPage = signal(false);
  currentUsername = () => this.auth.currentUser()?.username ?? null;

  ngOnInit(): void {
    const section = this.route.snapshot.paramMap.get('section') ?? '';
    this.isRootPage.set(this.route.snapshot.queryParamMap.get('root') === '1');
    this.author = this.currentUsername() ?? '';

    if (section) {
      this.api.listSections().subscribe(sections => {
        const sec = sections.find(s => s.slug === section);
        this.hasRawContent.set(sec?.hasRawContent ?? false);
        if (this.isRootPage() && !this.title.trim()) {
          this.title = sec?.title ?? '';
        }
      });
    }
  }

  create(): void {
    this.error.set('');
    if (!this.title.trim() || !this.content.trim()) {
      this.error.set('Title and content are required.');
      return;
    }
    const author = (this.currentUsername() ?? this.author).trim();
    if (!author) {
      this.error.set('Author name is required.');
      return;
    }

    const section = this.route.snapshot.paramMap.get('section') ?? '';
    const parentParts = ['path1', 'path2', 'path3', 'path4']
      .map(key => this.route.snapshot.paramMap.get(key))
      .filter((part): part is string => !!part);
    const parentPath = parentParts.length > 0 ? parentParts.join('/') : null;

    const request: any = { title: this.title.trim(), content: this.content.trim(), author };
    if (this.hasRawContent() && this.rawContent.trim()) {
      request.rawContent = this.rawContent.trim();
    }
    if (this.isRootPage()) {
      request.slug = SECTION_ROOT_SLUG;
    }

    const obs = this.isRootPage()
      ? this.api.createWikiPage(section, request)
      : parentPath
      ? this.api.createChildPage(section, parentPath, request)
      : this.api.createWikiPage(section, request);

    obs.subscribe({
      next: (result) => {
        if (this.isRootPage()) {
          this.router.navigate(['/wiki', section]);
          return;
        }
        const basePath = parentPath ? `${parentPath}/${result.slug}` : result.slug;
        this.router.navigate(['/wiki', section, ...basePath.split('/')]);
      },
      error: (err) => this.error.set(err.error || 'Failed to create page')
    });
  }

  cancel(): void {
    window.history.back();
  }
}
