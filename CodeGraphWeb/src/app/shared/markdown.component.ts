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

      :deep(h1) { font-size: 1.5rem; margin: 1.5rem 0 0.75rem; border-bottom: 1px solid var(--border-color, #333); padding-bottom: 0.3rem; }
      :deep(h2) { font-size: 1.3rem; margin: 1.25rem 0 0.5rem; }
      :deep(h3) { font-size: 1.1rem; margin: 1rem 0 0.5rem; }

      :deep(pre) {
        background: var(--surface-color, #1e1e2e); padding: 1rem; border-radius: 6px;
        overflow-x: auto; margin: 0.75rem 0; font-size: 0.85rem;
      }
      :deep(code) { font-family: 'Cascadia Code', 'Fira Code', monospace; }
      :deep(p code) {
        background: var(--surface-color, #1e1e2e); padding: 0.15rem 0.4rem; border-radius: 3px;
        font-size: 0.9em;
      }

      :deep(table) { border-collapse: collapse; width: 100%; margin: 0.75rem 0; }
      :deep(th), :deep(td) {
        border: 1px solid var(--border-color, #333); padding: 0.5rem 0.75rem; text-align: left;
      }
      :deep(th) { background: var(--surface-color, #1e1e2e); font-weight: 600; }

      :deep(a) { color: #a78bfa; }
      :deep(blockquote) {
        border-left: 3px solid var(--accent-color, #7c3aed); margin: 0.75rem 0;
        padding: 0.5rem 1rem; color: var(--muted-color, #888);
      }
      :deep(ul), :deep(ol) { padding-left: 1.5rem; }
      :deep(li) { margin: 0.25rem 0; }
      :deep(li ul), :deep(li ol) { margin-top: 0.25rem; }
      :deep(img) { max-width: 100%; border-radius: 6px; }
      :deep(hr) { border: none; border-top: 1px solid var(--border-color, #333); margin: 1.5rem 0; }
      :deep(p) { margin: 0.5rem 0; }
      :deep(strong) { font-weight: 600; }
      :deep(del) { text-decoration: line-through; color: var(--muted-color, #888); }
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
