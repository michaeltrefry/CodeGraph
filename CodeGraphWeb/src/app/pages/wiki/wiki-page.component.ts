import { Component, inject, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { MarkdownComponent } from '../../shared/markdown.component';

import { WikiPage, WikiRevisionListItem, WikiAttachment } from '../../core/models';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-wiki-page',
  standalone: true,
  imports: [FormsModule, RouterLink, DatePipe, MarkdownComponent],
  template: `
    @if (loading()) {
      <p>Loading...</p>
    } @else if (notFound()) {
      <div class="not-found">
        <h2>Page not found</h2>
        <a [routerLink]="['/wiki', sectionSlug(), '_new']">Create a page?</a>
      </div>
    } @else if (page()) {
      <div class="page-header">
        <div class="page-header-row">
          <h1>{{ page()!.title }}</h1>
          <div class="page-header-actions">
            <button class="primary" (click)="createSiblingPage()">New Page</button>
            <button
              class="primary"
              (click)="createChildPage()"
              [disabled]="!canCreateChildPage()"
              [title]="childPageDisabledReason()">
              New Child Page
            </button>
          </div>
        </div>
        <div class="page-meta">
          <span>v{{ page()!.revision }} by {{ page()!.author }}</span>
          <span>{{ page()!.updatedAt | date:'short' }}</span>
        </div>
      </div>

      @if (editing()) {
        <div class="edit-form">
          <input type="text" [(ngModel)]="editTitle" placeholder="Title" class="edit-title" />
          <label class="field-label">Content (Markdown)</label>
          <textarea [(ngModel)]="editContent" rows="20" class="edit-content" spellcheck="false"></textarea>

          @if (page()!.hasRawContent) {
            <label class="field-label">Raw Content</label>
            <textarea [(ngModel)]="editRawContent" rows="20" class="edit-content raw-editor" spellcheck="false"
              placeholder="Paste the raw file content here (e.g., skill definition, agent config)"></textarea>
          }

          <input type="text" [(ngModel)]="editAuthor" placeholder="Your name (required)" class="edit-author" />
          @if (saveError()) {
            <p class="save-error">{{ saveError() }}</p>
          }
          <div class="edit-actions">
            <button (click)="editing.set(false)">Cancel</button>
            <button class="primary" (click)="save()">Save</button>
          </div>
        </div>
      } @else {
        <app-markdown [content]="page()!.content" />

        @if (page()!.hasRawContent && page()!.rawContent) {
          <div class="raw-content-section">
            <div class="raw-content-header">
              <h3>Raw Content</h3>
              <button class="download-btn" (click)="downloadRawContent()">Download</button>
            </div>
            <pre class="raw-content-display">{{ page()!.rawContent }}</pre>
          </div>
        }

        <div class="page-actions">
          <button (click)="startEdit()">Edit</button>
          <button class="danger" (click)="deletePage()">Delete</button>
          <button (click)="toggleRevisions()">
            {{ showRevisions() ? 'Hide' : 'Show' }} Revisions
          </button>
        </div>
      }

      @if (showRevisions() && revisions().length > 0) {
        <div class="revisions">
          <h3>Revision History</h3>
          <ul>
            @for (rev of revisions(); track rev.revision) {
              <li>
                <strong>v{{ rev.revision }}</strong> — {{ rev.author }} — {{ rev.createdAt | date:'short' }}
              </li>
            }
          </ul>
        </div>
      }

      <div class="attachments">
        <h3>Attachments</h3>
        @for (att of attachments(); track att.id) {
          <div class="attachment-item">
            <a [href]="environment.baseUrl + att.downloadUrl" target="_blank">{{ att.filename }}</a>
            <span class="att-size">{{ formatSize(att.sizeBytes) }}</span>
            <button class="danger-sm" (click)="deleteAttachment(att.id)">x</button>
          </div>
        }
        <input type="file" (change)="uploadFile($event)" />
      </div>
    }
  `,
  styles: [`
    .page-header { margin-bottom: 1.5rem; }
    .page-header-row { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
    .page-header-row h1 { margin: 0 0 0.25rem; color: #111827; }
    .page-header-actions { display: flex; gap: 0.5rem; flex-shrink: 0; }
    .page-meta { font-size: 0.8rem; color: #6b7280; display: flex; gap: 1rem; }
    .page-actions { display: flex; gap: 0.5rem; margin-top: 1.5rem; padding-top: 1rem; border-top: 1px solid #e5e7eb; }
    button {
      padding: 0.4rem 0.8rem; border-radius: 6px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer; font-size: 0.85rem;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
    button.danger { background: #dc2626; border-color: transparent; color: white; }
    button.danger:hover { background: #b91c1c; }
    button.danger-sm { background: #dc2626; border-color: transparent; color: white; padding: 0.1rem 0.4rem; font-size: 0.75rem; }
    .edit-form { display: flex; flex-direction: column; gap: 0.75rem; }
    .edit-title {
      padding: 0.5rem; font-size: 1.1rem; border-radius: 6px;
      border: 1px solid #d1d5db; background: #f9fafb; color: #111827;
    }
    .field-label { font-size: 0.85rem; font-weight: 600; color: #374151; margin-bottom: -0.25rem; }
    .edit-content {
      padding: 0.75rem; font-family: 'Cascadia Code', monospace; font-size: 0.85rem;
      border-radius: 6px; border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827; resize: vertical;
    }
    .raw-editor { border-color: #6366f1; background: #faf5ff; }
    .edit-author {
      padding: 0.5rem; font-size: 0.9rem; border-radius: 6px;
      border: 1px solid #d1d5db; background: #f9fafb; color: #111827; max-width: 300px;
    }
    .save-error { color: #ef4444; font-size: 0.85rem; margin: 0; }
    .edit-actions { display: flex; gap: 0.5rem; }
    .raw-content-section {
      margin-top: 2rem; border: 1px solid #e5e7eb; border-radius: 8px; overflow: hidden;
    }
    .raw-content-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 0.75rem 1rem; background: #f9fafb; border-bottom: 1px solid #e5e7eb;
    }
    .raw-content-header h3 { margin: 0; font-size: 0.95rem; color: #374151; }
    .download-btn {
      padding: 0.3rem 0.75rem; font-size: 0.8rem; border-radius: 5px;
      background: #4f46e5; border-color: transparent; color: white; cursor: pointer;
    }
    .download-btn:hover { background: #4338ca; }
    .raw-content-display {
      margin: 0; padding: 1rem; font-family: 'Cascadia Code', monospace;
      font-size: 0.82rem; line-height: 1.5; background: #fefefe; color: #1f2937;
      overflow-x: auto; white-space: pre-wrap; word-wrap: break-word;
    }
    .revisions { margin-top: 1.5rem; }
    .revisions ul { list-style: none; padding: 0; }
    .revisions li { padding: 0.3rem 0; font-size: 0.85rem; color: #6b7280; }
    .attachments { margin-top: 1.5rem; }
    .attachment-item { display: flex; align-items: center; gap: 0.75rem; padding: 0.3rem 0; }
    .attachment-item a { color: #2563eb; text-decoration: none; }
    .attachment-item a:hover { text-decoration: underline; }
    .att-size { font-size: 0.8rem; color: #6b7280; }
    .not-found { text-align: center; margin-top: 4rem; color: #374151; }
    .not-found a { color: #2563eb; }
  `]
})
export class WikiPageComponent implements OnInit {
  readonly environment = environment;
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  page = signal<WikiPage | null>(null);
  loading = signal(true);
  notFound = signal(false);
  editing = signal(false);
  showRevisions = signal(false);
  revisions = signal<WikiRevisionListItem[]>([]);
  attachments = signal<WikiAttachment[]>([]);
  sectionSlug = signal('');
  pagePath = signal('');

  editTitle = '';
  editContent = '';
  editRawContent = '';
  editAuthor = '';

  ngOnInit(): void {
    this.route.url.subscribe(() => this.loadPage());
  }

  private loadPage(): void {
    // Build section and path from URL segments
    const urlSegments = this.route.snapshot.url;
    // The route is under /wiki/:section/**, so we get the full URL from root
    let fullUrl = this.router.url;
    // Strip /wiki/ prefix
    const wikiPrefix = '/wiki/';
    if (fullUrl.startsWith(wikiPrefix)) fullUrl = fullUrl.substring(wikiPrefix.length);
    // Strip query params
    const qIdx = fullUrl.indexOf('?');
    if (qIdx >= 0) fullUrl = fullUrl.substring(0, qIdx);

    const parts = fullUrl.split('/');
    const section = parts[0];
    const path = parts.slice(1).join('/');

    this.sectionSlug.set(section);
    this.pagePath.set(path);
    this.loading.set(true);
    this.notFound.set(false);

    if (!path) {
      this.loading.set(false);
      return;
    }

    this.api.getWikiPage(section, path).subscribe({
      next: page => {
        this.page.set(page);
        this.loading.set(false);
        this.loadAttachments(section, path);
      },
      error: () => {
        this.notFound.set(true);
        this.loading.set(false);
      }
    });
  }

  private loadAttachments(section: string, path: string): void {
    this.api.getWikiAttachments(section, path).subscribe({
      next: a => this.attachments.set(a),
      error: () => this.attachments.set([])
    });
  }

  startEdit(): void {
    this.editTitle = this.page()!.title;
    this.editContent = this.page()!.content;
    this.editRawContent = this.page()!.rawContent ?? '';
    this.editing.set(true);
  }

  saveError = signal('');

  save(): void {
    this.saveError.set('');
    if (!this.editAuthor.trim()) {
      this.saveError.set('Author name is required.');
      return;
    }
    const section = this.sectionSlug();
    const path = this.pagePath();
    const request: any = {
      title: this.editTitle,
      content: this.editContent,
      author: this.editAuthor.trim()
    };
    if (this.page()!.hasRawContent) {
      request.rawContent = this.editRawContent;
    }
    this.api.updateWikiPage(section, path, request).subscribe({
      next: () => {
        this.editing.set(false);
        this.loadPage();
      },
      error: (err) => {
        if (err.status === 409) {
          this.saveError.set('Conflict: this page was edited by someone else. Reload and try again.');
        } else if (err.status === 403) {
          this.saveError.set('You do not have permission to edit this page.');
        } else {
          this.saveError.set(err.error?.message || err.error || 'Save failed');
        }
      }
    });
  }

  createSiblingPage(): void {
    const section = this.sectionSlug();
    const path = this.pagePath();
    const parts = path.split('/');
    if (parts.length <= 1) {
      // Top-level page — sibling means new root page in the section
      this.router.navigate(['/wiki', section, '_new']);
    } else {
      // Navigate to parent/_new
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
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${page.slug}.md`;
    a.click();
    URL.revokeObjectURL(url);
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
}
