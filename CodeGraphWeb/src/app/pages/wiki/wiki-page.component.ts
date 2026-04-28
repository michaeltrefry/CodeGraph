import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { MarkdownComponent } from '../../shared/markdown.component';
import { WikiEditorFormComponent } from './wiki-editor-form.component';
import { getWikiSaveErrorMessage, WikiEditorState } from './wiki-editor';
import { WikiPage, WikiRevisionListItem, WikiAttachment } from '../../core/models';

@Component({
  selector: 'app-wiki-page',
  standalone: true,
  imports: [RouterLink, DatePipe, MarkdownComponent, WikiEditorFormComponent],
  template: `
    @if (loading()) {
      <p class="wk-loading cg-muted">Loading...</p>
    } @else if (notFound()) {
      <div class="wk-notfound">
        <h2>Page not found</h2>
        <a class="wk-btn primary" [routerLink]="['/wiki', sectionSlug(), '_new']">Create a page?</a>
      </div>
    } @else if (page()) {
      <header class="wk-header">
        <nav class="wk-breadcrumb">
          <a routerLink="/wiki">Wiki</a>
          <span class="sep">/</span>
          <a [routerLink]="['/wiki', sectionSlug()]">{{ sectionSlug() }}</a>
          <span class="sep">/</span>
          <span class="current">{{ page()!.title }}</span>
        </nav>

        <div class="wk-header-row">
          <h1 class="wk-title">{{ page()!.title }}</h1>
          <div class="wk-actions">
            <button class="wk-btn" type="button" (click)="createSiblingPage()">Sibling</button>
            <button
              class="wk-btn"
              type="button"
              (click)="createChildPage()"
              [disabled]="!canCreateChildPage()"
              [title]="childPageDisabledReason()"
            >
              Child
            </button>
          </div>
        </div>

        <div class="wk-meta">
          <span class="cg-muted cg-small">
            Updated by <span class="wk-meta-strong">{{ page()!.author }}</span> &middot;
            {{ page()!.updatedAt | date:'medium' }}
          </span>
          <span class="cg-chip cg-chip-mono">v{{ page()!.revision }}</span>
          @if (attachments().length > 0) {
            <span class="cg-chip cg-chip-mono">{{ attachments().length }} attach</span>
          }
        </div>
      </header>

      @if (editor.editing()) {
        <app-wiki-editor-form
          [title]="editor.title"
          (titleChange)="editor.title = $event"
          [content]="editor.content"
          (contentChange)="editor.content = $event"
          [rawContent]="editor.rawContent"
          (rawContentChange)="editor.rawContent = $event"
          [hasRawContent]="page()!.hasRawContent"
          [author]="editor.author"
          (authorChange)="editor.author = $event"
          [authorReadonly]="!!currentUsername()"
          [saveError]="editor.saveError()"
          (cancel)="editor.reset()"
          (save)="save()"
        />
      } @else {
        <article class="wk-article">
          <app-markdown [content]="page()!.content" />
        </article>

        @if (page()!.hasRawContent && page()!.rawContent) {
          <section class="wk-sub-card">
            <header class="wk-sub-head">
              <span class="wk-sub-label">Raw content</span>
              <button class="wk-btn" type="button" (click)="downloadRawContent()">Download</button>
            </header>
            <pre class="wk-raw-body">{{ page()!.rawContent }}</pre>
          </section>
        }

        <div class="wk-page-actions">
          <button class="wk-btn" type="button" (click)="startEdit()">Edit</button>
          <button class="wk-btn" type="button" (click)="toggleRevisions()">
            {{ showRevisions() ? 'Hide' : 'Show' }} revisions
          </button>
          <span class="wk-actions-spacer"></span>
          <button class="wk-btn danger" type="button" (click)="deletePage()">Delete</button>
        </div>
      }

      @if (showRevisions() && revisions().length > 0) {
        <section class="wk-sub-card">
          <header class="wk-sub-head">
            <span class="wk-sub-label">Revision history</span>
            <span class="cg-chip cg-chip-mono">{{ revisions().length }}</span>
          </header>
          <ul class="wk-rev-list">
            @for (rev of revisions(); track rev.revision) {
              <li class="wk-rev-row">
                <span class="cg-chip cg-chip-mono">v{{ rev.revision }}</span>
                <span class="wk-rev-author">{{ rev.author }}</span>
                <span class="cg-muted cg-small">{{ rev.createdAt | date:'medium' }}</span>
              </li>
            }
          </ul>
        </section>
      }

      <section class="wk-sub-card">
        <header class="wk-sub-head">
          <span class="wk-sub-label">Attachments</span>
          @if (attachments().length > 0) {
            <span class="cg-chip cg-chip-mono">{{ attachments().length }}</span>
          }
        </header>
        @if (attachments().length === 0) {
          <p class="wk-sub-empty cg-muted cg-small">No attachments yet.</p>
        } @else {
          <ul class="wk-att-list">
            @for (att of attachments(); track att.id) {
              <li class="wk-att-row">
                <button type="button" class="wk-att-name" (click)="downloadAttachment(att)">
                  {{ att.filename }}
                </button>
                <span class="wk-att-size cg-muted cg-xsmall cg-mono">{{ formatSize(att.sizeBytes) }}</span>
                <button
                  class="wk-att-delete"
                  type="button"
                  (click)="deleteAttachment(att.id)"
                  aria-label="Delete attachment"
                >
                  x
                </button>
              </li>
            }
          </ul>
        }
        <label class="wk-att-upload">
          <input type="file" (change)="uploadFile($event)" />
        </label>
      </section>
    }
  `,
  styles: [`
    :host {
      display: block;
      width: min(100%, 860px);
      max-width: 860px;
      margin: 0 auto;
    }

    .wk-loading { padding: 40px 0; }
    .wk-notfound {
      text-align: center;
      padding: 60px 20px;
      color: var(--text-2);
    }
    .wk-notfound h2 {
      margin: 0 0 16px;
      color: var(--text);
      font-weight: 600;
    }

    .wk-header { margin-bottom: 28px; }

    .wk-breadcrumb {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: var(--fs-sm);
      color: var(--muted);
      margin-bottom: 12px;
      flex-wrap: wrap;
    }
    .wk-breadcrumb a {
      color: var(--muted);
      text-decoration: none;
    }
    .wk-breadcrumb a:hover { color: var(--text); }
    .wk-breadcrumb .sep { color: var(--faint); }
    .wk-breadcrumb .current { color: var(--text); font-weight: 500; }

    .wk-header-row {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;
      flex-wrap: wrap;
    }

    .wk-title {
      margin: 0;
      font-size: 34px;
      font-weight: 600;
      color: var(--text);
      line-height: 1.15;
      flex: 1;
      min-width: 0;
    }

    .wk-actions {
      display: flex;
      gap: 6px;
      flex-shrink: 0;
      flex-wrap: wrap;
    }

    .wk-meta {
      display: flex;
      align-items: center;
      gap: 10px;
      margin-top: 12px;
      flex-wrap: wrap;
    }
    .wk-meta-strong { color: var(--text-2); font-weight: 500; }

    .wk-btn {
      appearance: none;
      min-height: 34px;
      padding: 6px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface);
      color: var(--text-2);
      font: inherit;
      font-size: var(--fs-sm);
      font-weight: 500;
      cursor: pointer;
      text-decoration: none;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
      white-space: nowrap;
    }
    .wk-btn:hover:not(:disabled) {
      background: var(--surface-2);
      color: var(--text);
      border-color: var(--border-2);
      text-decoration: none;
    }
    .wk-btn:disabled {
      opacity: 0.55;
      cursor: not-allowed;
    }
    .wk-btn.primary {
      background: var(--accent);
      color: white;
      border-color: var(--accent);
    }
    .wk-btn.primary:hover:not(:disabled) {
      background: color-mix(in oklab, var(--accent) 85%, black);
      color: white;
    }
    .wk-btn.danger {
      color: var(--sem-red);
      border-color: color-mix(in oklab, var(--sem-red) 30%, var(--border));
    }
    .wk-btn.danger:hover {
      background: var(--err-bg);
      border-color: color-mix(in oklab, var(--sem-red) 50%, var(--border));
      color: var(--sem-red);
    }

    .wk-article {
      color: var(--text-2);
      line-height: 1.65;
      font-size: 15px;
    }

    .wk-page-actions {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-top: 28px;
      padding-top: 16px;
      border-top: 1px solid var(--hairline);
      flex-wrap: wrap;
    }
    .wk-actions-spacer { flex: 1; }

    .wk-sub-card {
      margin-top: 20px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      overflow: hidden;
    }
    .wk-sub-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      padding: 10px 16px;
      background: var(--surface-2);
      border-bottom: 1px solid var(--hairline);
    }
    .wk-sub-label {
      font-size: var(--fs-xs);
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--muted);
    }
    .wk-sub-empty { padding: 12px 16px; margin: 0; }

    .wk-raw-body {
      margin: 0;
      padding: 14px 16px;
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.5;
      color: var(--text-2);
      overflow-x: auto;
      white-space: pre-wrap;
      word-wrap: break-word;
    }

    .wk-rev-list { list-style: none; padding: 0; margin: 0; }
    .wk-rev-row {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--hairline);
      font-size: var(--fs-sm);
      flex-wrap: wrap;
    }
    .wk-rev-row:last-child { border-bottom: 0; }
    .wk-rev-author { color: var(--text-2); font-weight: 500; }

    .wk-att-list { list-style: none; padding: 0; margin: 0; }
    .wk-att-row {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--hairline);
    }
    .wk-att-row:last-child { border-bottom: 0; }

    .wk-att-name {
      appearance: none;
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--accent-ink);
      font: inherit;
      font-size: var(--fs-sm);
      cursor: pointer;
      text-align: left;
      flex: 1;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .wk-att-name:hover { color: var(--accent); text-decoration: underline; }

    .wk-att-size { flex: 0 0 auto; }

    .wk-att-delete {
      appearance: none;
      width: 22px;
      height: 22px;
      border: 0;
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--muted);
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      flex: 0 0 auto;
      transition: background var(--transition), color var(--transition);
    }
    .wk-att-delete:hover {
      background: var(--err-bg);
      color: var(--sem-red);
    }

    .wk-att-upload {
      display: block;
      padding: 12px 16px;
      border-top: 1px solid var(--hairline);
    }
    .wk-att-upload input[type="file"] {
      font-family: inherit;
      font-size: var(--fs-sm);
      color: var(--text-2);
      max-width: 100%;
    }
  `]
})
export class WikiPageComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(AuthService);
  private destroyRef = inject(DestroyRef);
  private loadRequestId = 0;

  page = signal<WikiPage | null>(null);
  loading = signal(true);
  notFound = signal(false);
  showRevisions = signal(false);
  revisions = signal<WikiRevisionListItem[]>([]);
  attachments = signal<WikiAttachment[]>([]);
  sectionSlug = signal('');
  pagePath = signal('');
  editor = new WikiEditorState();
  currentUsername = () => this.auth.currentUser()?.username ?? null;

  ngOnInit(): void {
    this.route.url
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.loadPage());
  }

  private loadPage(): void {
    const requestId = ++this.loadRequestId;
    let fullUrl = this.router.url;
    const wikiPrefix = '/wiki/';
    if (fullUrl.startsWith(wikiPrefix)) fullUrl = fullUrl.substring(wikiPrefix.length);
    const qIdx = fullUrl.indexOf('?');
    if (qIdx >= 0) fullUrl = fullUrl.substring(0, qIdx);

    const parts = fullUrl.split('/');
    const section = parts[0];
    const path = parts.slice(1).join('/');

    this.sectionSlug.set(section);
    this.pagePath.set(path);
    this.loading.set(true);
    this.notFound.set(false);
    this.page.set(null);
    this.attachments.set([]);
    this.revisions.set([]);
    this.showRevisions.set(false);
    this.editor.reset();

    if (!path) {
      this.loading.set(false);
      return;
    }

    this.api.getWikiPage(section, path)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: page => {
          if (requestId !== this.loadRequestId) return;
          this.page.set(page);
          this.loading.set(false);
          this.loadAttachments(section, path, requestId);
        },
        error: () => {
          if (requestId !== this.loadRequestId) return;
          this.notFound.set(true);
          this.loading.set(false);
        }
      });
  }

  private loadAttachments(section: string, path: string, requestId = this.loadRequestId): void {
    this.api.getWikiAttachments(section, path)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: a => {
          if (requestId !== this.loadRequestId) return;
          this.attachments.set(a);
        },
        error: () => {
          if (requestId !== this.loadRequestId) return;
          this.attachments.set([]);
        }
      });
  }

  startEdit(): void {
    this.editor.start(this.page()!, this.currentUsername());
  }

  save(): void {
    const section = this.sectionSlug();
    const path = this.pagePath();
    this.editor.saveError.set('');

    const { request, validationError } = this.editor.buildRequest({
      author: this.currentUsername(),
      hasRawContent: this.page()!.hasRawContent,
      requireTitleAndContent: true
    });
    if (!request) {
      this.editor.saveError.set(validationError ?? 'Save failed');
      return;
    }

    this.api.updateWikiPage(section, path, request).subscribe({
      next: () => {
        this.editor.reset();
        this.loadPage();
      },
      error: (err) => {
        this.editor.saveError.set(getWikiSaveErrorMessage(err, {
          409: 'Conflict: this page was edited by someone else. Reload and try again.',
          403: 'You do not have permission to edit this page.'
        }));
      }
    });
  }

  createSiblingPage(): void {
    const section = this.sectionSlug();
    const path = this.pagePath();
    const parts = path.split('/');
    if (parts.length <= 1) {
      this.router.navigate(['/wiki', section, '_new']);
    } else {
      const parentPath = parts.slice(0, -1);
      this.router.navigate(['/wiki', section, ...parentPath, '_new']);
    }
  }

  createChildPage(): void {
    if (!this.canCreateChildPage()) return;
    const section = this.sectionSlug();
    const path = this.pagePath();
    this.router.navigate(['/wiki', section, ...path.split('/'), '_new']);
  }

  canCreateChildPage(): boolean {
    return (this.page()?.depth ?? 0) < 3;
  }

  childPageDisabledReason(): string {
    return this.canCreateChildPage() ? 'Create a child page' : 'Maximum wiki depth reached';
  }

  deletePage(): void {
    if (!confirm('Delete this page and all children?')) return;
    const section = this.sectionSlug();
    const path = this.pagePath();
    this.api.deleteWikiPage(section, path).subscribe({
      next: () => this.router.navigate(['/wiki', section]),
      error: (err) => alert(err.error || 'Delete failed')
    });
  }

  toggleRevisions(): void {
    this.showRevisions.update(v => !v);
    if (this.showRevisions() && this.revisions().length === 0) {
      this.api.getWikiRevisions(this.sectionSlug(), this.pagePath()).subscribe(r => this.revisions.set(r));
    }
  }

  downloadRawContent(): void {
    const page = this.page();
    if (!page?.rawContent) return;
    const blob = new Blob([page.rawContent], { type: 'text/plain' });
    this.saveBlob(blob, `${page.slug}.md`);
  }

  downloadAttachment(att: WikiAttachment): void {
    this.api.downloadWikiAttachment(att.downloadUrl).subscribe({
      next: blob => this.saveBlob(blob, att.filename),
      error: (err) => alert(err.error || 'Download failed')
    });
  }

  uploadFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files?.length) return;
    const file = input.files[0];
    this.api.uploadWikiAttachment(this.sectionSlug(), this.pagePath(), file).subscribe({
      next: () => this.loadAttachments(this.sectionSlug(), this.pagePath()),
      error: (err) => alert(err.error || 'Upload failed')
    });
  }

  deleteAttachment(id: number): void {
    if (!confirm('Delete this attachment?')) return;
    this.api.deleteWikiAttachment(id).subscribe({
      next: () => this.loadAttachments(this.sectionSlug(), this.pagePath()),
      error: (err) => alert(err.error || 'Delete failed')
    });
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private saveBlob(blob: Blob, filename: string): void {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  }
}
