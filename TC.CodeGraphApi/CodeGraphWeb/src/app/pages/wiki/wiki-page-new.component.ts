import { Component, inject, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';

@Component({
  selector: 'app-wiki-page-new',
  standalone: true,
  imports: [FormsModule],
  template: `
    <h2>New Page</h2>
    <div class="form">
      <input type="text" [(ngModel)]="title" placeholder="Page title" class="form-input" />
      <label class="field-label">Content (Markdown)</label>
      <textarea [(ngModel)]="content" rows="20" placeholder="Page content (markdown)" class="form-textarea" spellcheck="false"></textarea>

      @if (hasRawContent()) {
        <label class="field-label">Raw Content</label>
        <textarea [(ngModel)]="rawContent" rows="20" class="form-textarea raw-editor"
          placeholder="Paste the raw file content here (e.g., skill definition, agent config)" spellcheck="false"></textarea>
      }

      <input type="text" [(ngModel)]="author" placeholder="Your name (required)" class="form-author" />

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      <div class="form-actions">
        <button (click)="cancel()">Cancel</button>
        <button class="primary" (click)="create()">Create</button>
      </div>
    </div>
  `,
  styles: [`
    .form { display: flex; flex-direction: column; gap: 0.75rem; max-width: 800px; }
    .form-input {
      padding: 0.5rem; font-size: 1.1rem; border-radius: 6px;
      border: 1px solid #d1d5db; background: #f9fafb; color: #111827;
    }
    .field-label { font-size: 0.85rem; font-weight: 600; color: #374151; margin-bottom: -0.25rem; }
    .form-textarea {
      padding: 0.75rem; font-family: 'Cascadia Code', monospace; font-size: 0.85rem;
      border-radius: 6px; border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827; resize: vertical;
    }
    .raw-editor { border-color: #6366f1; background: #faf5ff; }
    .form-actions { display: flex; gap: 0.5rem; }
    button {
      padding: 0.4rem 0.8rem; border-radius: 6px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
    .form-author {
      padding: 0.5rem; font-size: 0.9rem; border-radius: 6px;
      border: 1px solid #d1d5db; background: #f9fafb; color: #111827; max-width: 300px;
    }
    .error { color: #ef4444; }
  `]
})
export class WikiPageNewComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  title = '';
  content = '';
  rawContent = '';
  author = '';
  error = signal('');
  hasRawContent = signal(false);

  ngOnInit(): void {
    // Determine section from URL and check if it supports raw content
    const url = this.router.url;
    const match = url.match(/^\/wiki\/([^/]+)/);
    const section = match ? match[1] : '';
    if (section) {
      this.api.listSections().subscribe(sections => {
        const sec = sections.find(s => s.slug === section);
        this.hasRawContent.set(sec?.hasRawContent ?? false);
      });
    }
  }

  create(): void {
    if (!this.title.trim() || !this.content.trim()) {
      this.error.set('Title and content are required.');
      return;
    }
    if (!this.author.trim()) {
      this.error.set('Author name is required.');
      return;
    }

    // Determine section from URL
    const url = this.router.url;
    const match = url.match(/^\/wiki\/([^/]+)/);
    const section = match ? match[1] : '';

    // Determine if creating under a parent path
    const parentMatch = url.match(/^\/wiki\/[^/]+\/(.+)\/_new$/);
    const parentPath = parentMatch ? parentMatch[1] : null;

    const request: any = { title: this.title.trim(), content: this.content.trim(), author: this.author.trim() };
    if (this.hasRawContent() && this.rawContent.trim()) {
      request.rawContent = this.rawContent.trim();
    }

    const obs = parentPath
      ? this.api.createChildPage(section, parentPath, request)
      : this.api.createWikiPage(section, request);

    obs.subscribe({
      next: (result) => {
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
