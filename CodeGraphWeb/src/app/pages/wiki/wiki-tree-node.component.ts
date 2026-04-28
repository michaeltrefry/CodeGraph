import { Component, Input, OnInit, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { NgTemplateOutlet } from '@angular/common';
import { WikiTreeNode } from '../../core/models';

@Component({
  selector: 'app-wiki-tree-node',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, NgTemplateOutlet],
  template: `
    <ng-template #nodeTemplate let-node let-parentPath="parentPath" let-sectionSlug="sectionSlug">
      <li class="wkn-node">
        @if (node.children && node.children.length > 0) {
          <div class="wkn-row" [style.padding-left.px]="node.depth * 12 + 6">
            <button
              class="wkn-caret"
              type="button"
              (click)="toggle(node.id)"
              [attr.aria-label]="expandedIds().has(node.id) ? 'Collapse' : 'Expand'"
            >
              {{ expandedIds().has(node.id) ? 'v' : '>' }}
            </button>
            <a
              [routerLink]="getRouteParts(sectionSlug, parentPath, node.slug)"
              routerLinkActive="active"
              class="wkn-link wkn-folder"
            >
              {{ node.title }}
            </a>
          </div>
          @if (expandedIds().has(node.id)) {
            <ul class="wkn-tree-list">
              @for (child of node.children; track child.id) {
                <ng-container
                  *ngTemplateOutlet="nodeTemplate; context: { $implicit: child, parentPath: parentPath + '/' + node.slug, sectionSlug: sectionSlug }"
                />
              }
            </ul>
          }
        } @else {
          <div class="wkn-row" [style.padding-left.px]="node.depth * 12 + 26">
            <a
              [routerLink]="getRouteParts(sectionSlug, parentPath, node.slug)"
              routerLinkActive="active"
              class="wkn-link"
            >
              {{ node.title }}
            </a>
          </div>
        }
      </li>
    </ng-template>

    <ng-container
      *ngTemplateOutlet="nodeTemplate; context: { $implicit: node, parentPath: parentPath, sectionSlug: sectionSlug }"
    />
  `,
  styles: [`
    .wkn-node { list-style: none; }
    .wkn-tree-list { list-style: none; padding: 0; margin: 0; }

    .wkn-row {
      display: flex;
      align-items: center;
      gap: 2px;
      margin: 1px 0;
      position: relative;
    }

    .wkn-caret {
      appearance: none;
      width: 18px;
      height: 20px;
      border: 0;
      background: transparent;
      color: var(--faint);
      cursor: pointer;
      font-size: 10px;
      line-height: 1;
      display: grid;
      place-items: center;
      border-radius: var(--radius-sm);
      flex: 0 0 auto;
      transition: background var(--transition), color var(--transition);
    }
    .wkn-caret:hover { color: var(--muted); background: var(--surface-2); }

    .wkn-link {
      flex: 1;
      min-width: 0;
      padding: 4px 8px;
      border-radius: var(--radius-sm);
      color: var(--text-2);
      text-decoration: none;
      font-size: var(--fs-sm);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      transition: background var(--transition), color var(--transition);
      position: relative;
    }
    .wkn-link:hover {
      background: var(--surface-2);
      color: var(--text);
      text-decoration: none;
    }

    .wkn-link.active {
      background: var(--accent-weak);
      color: var(--accent-ink);
    }

    .wkn-link.active::before {
      content: '';
      position: absolute;
      left: -8px;
      top: 4px;
      bottom: 4px;
      width: 2px;
      background: var(--accent);
      border-radius: 0 2px 2px 0;
    }

    .wkn-folder { font-weight: 500; }
  `]
})
export class WikiTreeNodeComponent implements OnInit {
  @Input() node!: WikiTreeNode;
  @Input() sectionSlug = '';
  @Input() parentPath = '';

  expandedIds = signal<Set<number>>(new Set());

  ngOnInit(): void {
    const ids = new Set<number>();
    this.collectExpandableIds(this.node, ids);
    if (ids.size > 0) {
      this.expandedIds.set(ids);
    }
  }

  private collectExpandableIds(node: WikiTreeNode, ids: Set<number>): void {
    if (node.children && node.children.length > 0) {
      ids.add(node.id);
      for (const child of node.children) {
        this.collectExpandableIds(child, ids);
      }
    }
  }

  toggle(id: number): void {
    this.expandedIds.update(ids => {
      const next = new Set(ids);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  getRouteParts(sectionSlug: string, parentPath: string, slug: string): string[] {
    const fullPath = `${parentPath}/${slug}`;
    return ['/wiki', ...fullPath.split('/')];
  }
}
