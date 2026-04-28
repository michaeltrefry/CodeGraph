import { Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { WikiSection, WikiTreeNode } from '../../core/models';
import { WikiTreeNodeComponent } from './wiki-tree-node.component';

@Component({
  selector: 'app-wiki-sidebar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, WikiTreeNodeComponent],
  template: `
    <nav class="wiki-sidebar" [class.collapsed]="collapsed()">
      <div class="sidebar-header">
        <span class="sidebar-title">AI Wiki</span>
        <button class="toggle-btn" type="button" (click)="toggleCollapse()">{{ collapsed() ? '>' : '<' }}</button>
      </div>

      @if (!collapsed()) {
        <ul class="section-list">
          @for (section of sections; track section.id) {
            <li>
              <div class="section-row">
                <a
                  class="section-link"
                  [class.expanded]="expandedSection() === section.slug"
                  [routerLink]="['/wiki', section.slug]"
                  routerLinkActive="active"
                  (click)="openSection(section)"
                >
                  {{ section.title }}
                </a>
                <button
                  type="button"
                  class="section-toggle"
                  [attr.aria-label]="expandedSection() === section.slug ? 'Collapse section' : 'Expand section'"
                  (click)="toggleSection(section)"
                >
                  <span class="chevron">{{ expandedSection() === section.slug ? '-' : '+' }}</span>
                </button>
              </div>

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
      width: 260px;
      min-width: 260px;
      padding: 12px;
      background: var(--surface);
      border-right: 1px solid var(--border);
      overflow-y: auto;
      transition: width var(--transition), min-width var(--transition), background var(--transition);
    }
    .wiki-sidebar.collapsed {
      width: 40px;
      min-width: 40px;
      padding: 12px 4px;
    }
    .sidebar-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 12px;
    }
    .sidebar-title {
      font-weight: 600;
      font-size: var(--fs-sm);
      color: var(--text);
    }
    .collapsed .sidebar-title { display: none; }
    .toggle-btn {
      appearance: none;
      width: 28px;
      height: 28px;
      display: grid;
      place-items: center;
      background: transparent;
      border: 0;
      border-radius: var(--radius-sm);
      color: var(--muted);
      cursor: pointer;
      font-size: var(--fs-md);
      transition: background var(--transition), color var(--transition);
    }
    .toggle-btn:hover {
      background: var(--surface-2);
      color: var(--text);
    }
    .section-list {
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .section-row {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .section-link {
      flex: 1;
      min-width: 0;
      padding: 6px 8px;
      border-radius: var(--radius-sm);
      color: var(--text-2);
      cursor: pointer;
      font-size: var(--fs-sm);
      font-weight: 500;
      text-decoration: none;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      transition: background var(--transition), color var(--transition);
    }
    .section-link:hover {
      background: var(--surface-2);
      color: var(--text);
      text-decoration: none;
    }
    .section-link.expanded, .section-link.active {
      background: var(--accent-weak);
      color: var(--accent-ink);
    }
    .section-toggle {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 28px;
      height: 28px;
      border: 0;
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--muted);
      cursor: pointer;
      flex-shrink: 0;
      transition: background var(--transition), color var(--transition);
    }
    .section-toggle:hover {
      background: var(--surface-2);
      color: var(--text);
    }
    .chevron { font-size: var(--fs-xs); opacity: 0.7; }
    .tree-list {
      list-style: none;
      padding: 0;
      margin: 4px 0 0;
    }
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

  openSection(section: WikiSection): void {
    if (this.expandedSection() !== section.slug) {
      this.expandedSection.set(section.slug);
      this.treeRequested.emit(section.slug);
    }

    this.sectionToggled.emit(section);
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
