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
      <li class="tree-node" [style.padding-left.rem]="node.depth * 0.75 + 0.5">
        @if (node.children && node.children.length > 0) {
          <div class="folder-row">
            <a [routerLink]="getRouteParts(sectionSlug, parentPath, node.slug)" routerLinkActive="active" class="page-link folder">
              {{ node.title }}
            </a>
            <button class="folder-toggle" (click)="toggle(node.id)">
              {{ expandedIds().has(node.id) ? '−' : '+' }}
            </button>
          </div>
          @if (expandedIds().has(node.id)) {
            <ul class="tree-list">
              @for (child of node.children; track child.id) {
                <ng-container
                  *ngTemplateOutlet="nodeTemplate; context: { $implicit: child, parentPath: parentPath + '/' + node.slug, sectionSlug: sectionSlug }"
                />
              }
            </ul>
          }
        } @else {
          <a [routerLink]="getRouteParts(sectionSlug, parentPath, node.slug)" routerLinkActive="active" class="page-link">
            {{ node.title }}
          </a>
        }
      </li>
    </ng-template>

    <ng-container
      *ngTemplateOutlet="nodeTemplate; context: { $implicit: node, parentPath: parentPath, sectionSlug: sectionSlug }"
    />
  `,
  styles: [`
    .tree-node { margin: 1px 0; list-style: none; }
    .tree-list { list-style: none; padding: 0; margin: 0; }
    .folder-row { display: flex; align-items: center; justify-content: space-between; gap: 0.2rem; }
    .folder-toggle {
      background: none; border: none; color: #9ca3af; cursor: pointer;
      padding: 0; font-size: 0.8rem; width: 1rem; flex-shrink: 0;
    }
    .folder-toggle:hover { color: #374151; }
    .page-link {
      text-decoration: none; color: #6b7280;
      font-size: 0.85rem; padding: 0.15rem 0.4rem; border-radius: 3px;
      display: inline-block;
    }
    .page-link:hover { color: #111827; }
    .page-link.active { color: #5b21b6; background: #ede9fe; }
    .page-link.folder { font-weight: 500; }
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
