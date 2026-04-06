import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { WikiSection, WikiTreeNode } from '../../core/models';

@Component({
  selector: 'app-wiki-section',
  standalone: true,
  imports: [RouterLink],
  template: `
    @if (section()) {
      <h1>{{ section()!.title }}</h1>
      @if (section()!.description) {
        <p class="description">{{ section()!.description }}</p>
      }

      @if (pages().length > 0) {
        <ul class="page-list">
          @for (page of pages(); track page.id) {
            <li>
              <a [routerLink]="['/wiki', section()!.slug, page.slug]">{{ page.title }}</a>
            </li>
          }
        </ul>
      } @else {
        <p class="empty">No pages yet.</p>
      }

      @if (section()!.allowUserPages) {
        <a class="new-btn" [routerLink]="['/wiki', section()!.slug, '_new']">+ New Page</a>
      }
    }
  `,
  styles: [`
    .description { color: #6b7280; margin-bottom: 1rem; }
    .page-list { list-style: none; padding: 0; }
    .page-list li { padding: 0.5rem 0; border-bottom: 1px solid #e5e7eb; }
    .page-list a { text-decoration: none; color: #2563eb; font-weight: 500; }
    .page-list a:hover { text-decoration: underline; }
    .empty { color: #9ca3af; }
    .new-btn {
      display: inline-block; margin-top: 1rem; padding: 0.5rem 1rem;
      background: #2563eb; color: white; border-radius: 6px;
      text-decoration: none; font-size: 0.9rem;
    }
    .new-btn:hover { background: #1d4ed8; text-decoration: none; }
  `]
})
export class WikiSectionComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  section = signal<WikiSection | null>(null);
  pages = signal<WikiTreeNode[]>([]);

  ngOnInit(): void {
    this.route.paramMap.subscribe(params => {
      const slug = params.get('section');
      if (!slug) return;

      this.api.listSections().subscribe(sections => {
        this.section.set(sections.find(s => s.slug === slug) ?? null);
      });

      this.api.getSectionTree(slug).subscribe(tree => this.pages.set(tree));
    });
  }
}
