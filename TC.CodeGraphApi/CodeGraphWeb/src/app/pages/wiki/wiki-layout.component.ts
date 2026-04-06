import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterOutlet } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { WikiSection, WikiTreeNode } from '../../core/models';
import { WikiSidebarComponent } from './wiki-sidebar.component';

@Component({
  selector: 'app-wiki-layout',
  standalone: true,
  imports: [RouterOutlet, WikiSidebarComponent],
  template: `
    <div class="wiki-layout">
      <app-wiki-sidebar
        [sections]="sections()"
        [expandedSection]="expandedSection"
        [tree]="tree"
        (treeRequested)="loadTree($event)"
      />
      <div class="wiki-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    .wiki-layout { display: flex; height: 100%; }
    .wiki-content { flex: 1; min-width: 0; overflow-y: auto; padding: 1.5rem 2rem; }
  `]
})
export class WikiLayoutComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);

  sections = signal<WikiSection[]>([]);
  expandedSection = signal<string | null>(null);
  tree = signal<WikiTreeNode[] | null>(null);

  ngOnInit(): void {
    this.api.listSections().subscribe(s => this.sections.set(s));

    // Auto-expand section from URL
    const sectionSlug = this.route.firstChild?.snapshot?.paramMap?.get('section');
    if (sectionSlug) {
      this.expandedSection.set(sectionSlug);
      this.loadTree(sectionSlug);
    }
  }

  loadTree(sectionSlug: string): void {
    this.api.getSectionTree(sectionSlug).subscribe(t => this.tree.set(t));
  }
}
