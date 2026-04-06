import { Component, inject, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { SearchResultItem, LABEL_ICONS } from '../../core/models';

@Component({
  selector: 'app-search',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './search.component.html',
  styleUrl: './search.component.scss'
})
export class SearchComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  query = signal('');
  results = signal<SearchResultItem[]>([]);
  total = signal(0);
  page = signal(1);
  pageSize = 25;
  loading = signal(false);

  ngOnInit() {
    this.route.queryParams.subscribe(params => {
      const q = params['q'] || '';
      const page = +(params['page'] || 1);
      this.query.set(q);
      this.page.set(page);
      if (q) this.doSearch(q, page);
    });
  }

  private doSearch(q: string, page: number) {
    this.loading.set(true);
    this.api.search(q, page, this.pageSize).subscribe(res => {
      this.loading.set(false);

      // Single result — navigate directly
      if (res.total === 1 && res.items.length === 1) {
        this.navigateToItem(res.items[0]);
        return;
      }

      this.results.set(res.items);
      this.total.set(res.total);
    });
  }

  getIcon(item: SearchResultItem): string {
    if (item.type === 'repository') return '📦';
    return (item.nodeLabel && LABEL_ICONS[item.nodeLabel]) || '🔹';
  }

  getTypeLabel(item: SearchResultItem): string {
    if (item.type === 'repository') return 'Repository';
    return item.nodeLabel || 'Node';
  }

  getLink(item: SearchResultItem): string {
    if (item.type === 'repository') return `/repos/${item.name}`;
    return `/nodes/${item.nodeId}`;
  }

  get totalPages(): number {
    return Math.ceil(this.total() / this.pageSize);
  }

  goToPage(p: number) {
    this.router.navigate([], {
      queryParams: { q: this.query(), page: p },
      queryParamsHandling: 'merge'
    });
  }

  private navigateToItem(item: SearchResultItem) {
    if (item.type === 'repository') {
      this.router.navigate(['/repos', item.name], { replaceUrl: true });
    } else if (item.nodeId) {
      this.router.navigate(['/nodes', item.nodeId], { replaceUrl: true });
    }
  }
}
