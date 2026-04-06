import { Component, Input, Output, EventEmitter, signal } from '@angular/core';
import { WikiSection, WikiTreeNode } from '../../core/models';
import { WikiTreeNodeComponent } from './wiki-tree-node.component';

@Component({
  selector: 'app-wiki-sidebar',
  standalone: true,
  imports: [WikiTreeNodeComponent],
  template: `
    <nav class="wiki-sidebar" [class.collapsed]="collapsed()">
      <div class="sidebar-header">
        <span class="sidebar-title">AI Wiki</span>
        <button class="toggle-btn" (click)="toggleCollapse()">{{ collapsed() ? '>' : '<' }}</button>
      </div>

      @if (!collapsed()) {
        <ul class="section-list">
          @for (section of sections; track section.id) {
            <li>
              <button
                class="section-btn"
                [class.expanded]="expandedSection() === section.slug"
                (click)="toggleSection(section)"
              >
                <span>{{ section.title }}</span>
                <span class="chevron">{{ expandedSection() === section.slug ? '−' : '+' }}</span>
              </button>

              @if (expandedSection() === section.slug && tree()) {
                <ul class="tree-list">
                  @for (node of tree()!; track node.id) {
                    <app-wiki-tree-node [node]="node" [sectionSlug]="section.slug" [parentPath]="section.slug" />
                  }
                </ul>
              }
            </li>
          }
        </ul>
      }
    </nav>
  `,
  styles: [`
    .wiki-sidebar {
      width: 260px; min-width: 260px; padding: 0.75rem;
      background: white; border-right: 1px solid #e5e7eb;
      overflow-y: auto; transition: width 0.2s, min-width 0.2s;
    }
    .wiki-sidebar.collapsed { width: 40px; min-width: 40px; padding: 0.75rem 0.25rem; }
    .sidebar-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .sidebar-title { font-weight: 600; font-size: 0.95rem; color: #111827; }
    .collapsed .sidebar-title { display: none; }
    .toggle-btn {
      background: none; border: none; color: #6b7280; cursor: pointer;
      font-size: 1rem; padding: 0.25rem;
    }
    .toggle-btn:hover { color: #111827; }
    .section-list { list-style: none; padding: 0; margin: 0; }
    .section-btn {
      display: flex; justify-content: space-between; align-items: center;
      width: 100%; padding: 0.4rem 0.5rem; border: none; border-radius: 4px;
      background: none; color: #374151; cursor: pointer; font-size: 0.9rem; font-weight: 500;
    }
    .section-btn:hover { background: #f3f4f6; }
    .section-btn.expanded { background: #ede9fe; color: #5b21b6; }
    .chevron { font-size: 0.8rem; opacity: 0.6; }
    .tree-list { list-style: none; padding: 0; margin: 0.25rem 0 0; }
  `]
})
export class WikiSidebarComponent {
  @Input() sections: WikiSection[] = [];
  @Input() expandedSection = signal<string | null>(null);
  @Input() tree = signal<WikiTreeNode[] | null>(null);
  @Output() sectionToggled = new EventEmitter<WikiSection>();
  @Output() treeRequested = new EventEmitter<string>();

  collapsed = signal(false);

  toggleCollapse(): void {
    this.collapsed.update(v => !v);
  }

  toggleSection(section: WikiSection): void {
    if (this.expandedSection() === section.slug) {
      this.expandedSection.set(null);
      this.tree.set(null);
    } else {
      this.expandedSection.set(section.slug);
      this.treeRequested.emit(section.slug);
    }
    this.sectionToggled.emit(section);
  }
}
