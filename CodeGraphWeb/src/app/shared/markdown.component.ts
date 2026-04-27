import { Component, Input, OnChanges, signal } from '@angular/core';
import { Marked } from 'marked';
import hljs from 'highlight.js';

const marked = new Marked({
  renderer: {
    code({ text, lang }) {
      const language = lang && hljs.getLanguage(lang) ? lang : 'plaintext';
      const highlighted = hljs.highlight(text, { language }).value;
      return `<pre><code class="hljs language-${language}">${highlighted}</code></pre>`;
    }
  }
});

@Component({
  selector: 'app-markdown',
  standalone: true,
  template: `<div class="markdown-body" [innerHTML]="html()"></div>`,
  styles: [`
    .markdown-body {
      line-height: 1.6;
      color: var(--text-2);
    }

    :host ::ng-deep .markdown-body h1 {
      font-size: 1.5rem;
      margin: 1.5rem 0 0.75rem;
      border-bottom: 1px solid var(--border);
      padding-bottom: 0.3rem;
      color: var(--text);
    }

    :host ::ng-deep .markdown-body h2 {
      font-size: 1.3rem;
      margin: 1.25rem 0 0.5rem;
      color: var(--text);
    }

    :host ::ng-deep .markdown-body h3 {
      font-size: 1.1rem;
      margin: 1rem 0 0.5rem;
      color: var(--text);
    }

    :host ::ng-deep .markdown-body pre {
      background: var(--surface-3);
      color: var(--text-2);
      padding: 1rem;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      overflow-x: auto;
      margin: 0.75rem 0;
      font-size: 0.85rem;
    }

    :host ::ng-deep .markdown-body code {
      font-family: var(--font-mono);
    }

    :host ::ng-deep .markdown-body p code {
      background: var(--surface-3);
      color: var(--text-2);
      padding: 0.15rem 0.4rem;
      border: 1px solid var(--border);
      border-radius: 3px;
      font-size: 0.9em;
    }

    :host ::ng-deep .markdown-body table {
      border-collapse: collapse;
      width: 100%;
      margin: 0.75rem 0;
    }

    :host ::ng-deep .markdown-body th,
    :host ::ng-deep .markdown-body td {
      border: 1px solid var(--border);
      padding: 0.5rem 0.75rem;
      text-align: left;
    }

    :host ::ng-deep .markdown-body th {
      background: var(--surface-2);
      color: var(--muted);
      font-weight: 600;
    }

    :host ::ng-deep .markdown-body a {
      color: var(--accent-ink);
    }

    :host ::ng-deep .markdown-body blockquote {
      border-left: 3px solid var(--accent-dim);
      margin: 0.75rem 0;
      padding: 0.5rem 1rem;
      color: var(--muted);
    }

    :host ::ng-deep .markdown-body ul,
    :host ::ng-deep .markdown-body ol {
      padding-left: 1.5rem;
    }

    :host ::ng-deep .markdown-body li {
      margin: 0.25rem 0;
    }

    :host ::ng-deep .markdown-body li ul,
    :host ::ng-deep .markdown-body li ol {
      margin-top: 0.25rem;
    }

    :host ::ng-deep .markdown-body img {
      max-width: 100%;
      border-radius: var(--radius);
    }

    :host ::ng-deep .markdown-body hr {
      border: none;
      border-top: 1px solid var(--border);
      margin: 1.5rem 0;
    }

    :host ::ng-deep .markdown-body p {
      margin: 0.5rem 0;
    }

    :host ::ng-deep .markdown-body strong {
      color: var(--text);
      font-weight: 600;
    }

    :host ::ng-deep .markdown-body del {
      text-decoration: line-through;
      color: var(--muted);
    }
  `]
})
export class MarkdownComponent implements OnChanges {
  @Input() content = '';
  html = signal('');

  ngOnChanges(): void {
    const rendered = marked.parse(this.content || '');
    this.html.set(typeof rendered === 'string' ? rendered : '');
  }
}
