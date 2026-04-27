import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { ProjectInfo } from '../../core/models';
import { TypeaheadComponent, TypeaheadItem } from '../../shared/typeahead.component';

@Component({
  selector: 'app-repos',
  imports: [FormsModule, RouterLink, TypeaheadComponent],
  templateUrl: './repos.component.html',
  styleUrl: './repos.component.scss'
})
export class ReposComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  search = signal('');
  group = signal('');
  page = signal(1);
  pageSize = 25;
  items = signal<ProjectInfo[]>([]);
  total = signal(0);
  groups = signal<string[]>([]);
  groupItems = computed<TypeaheadItem[]>(() =>
    this.groups().map(g => ({ value: g, label: g }))
  );
  loading = signal(false);

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit() {
    const params = this.route.snapshot.queryParamMap;
    const p = Number(params.get('page'));
    if (p > 0) this.page.set(p);
    const q = params.get('search') ?? '';
    if (q) this.search.set(q);
    const g = params.get('group') ?? '';
    if (g) this.group.set(g);
    this.load();
  }

  onSearchInput(value: string) {
    this.search.set(value);
    this.page.set(1);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.load(), 300);
  }

  onGroupChange(value: string) {
    this.group.set(value);
    this.page.set(1);
    this.load();
  }

  load() {
    this.loading.set(true);
    this.updateUrl();
    this.api.listProjects(this.search() || undefined, this.group() || undefined, this.page(), this.pageSize)
      .subscribe({
        next: r => {
          this.items.set(r.items);
          this.total.set(r.total);
          this.groups.set(r.groups);
          this.loading.set(false);
        },
        error: () => this.loading.set(false)
      });
  }

  totalPages() {
    return Math.max(1, Math.ceil(this.total() / this.pageSize));
  }

  prevPage() {
    if (this.page() > 1) { this.page.update(p => p - 1); this.load(); }
  }

  nextPage() {
    if (this.page() < this.totalPages()) { this.page.update(p => p + 1); this.load(); }
  }

  formatDate(d?: string) {
    return d ? new Date(d).toLocaleDateString() : '—';
  }

  relTime(iso?: string): string {
    if (!iso) return '-';
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return '-';
    const diffSec = Math.round((Date.now() - then) / 1000);
    const rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
    const table: [Intl.RelativeTimeFormatUnit, number][] = [
      ['second', 60], ['minute', 60], ['hour', 24], ['day', 30], ['month', 12],
    ];
    let value = diffSec;
    let unit: Intl.RelativeTimeFormatUnit = 'second';
    for (const [u, step] of table) {
      unit = u;
      if (Math.abs(value) < step) break;
      value = Math.round(value / step);
    }
    if (unit === 'month' && Math.abs(value) >= 12) {
      value = Math.round(value / 12);
      unit = 'year';
    }
    return rtf.format(-value, unit);
  }

  languageKey(language?: string): string {
    if (!language) return '';
    const key = language.trim().toLowerCase();
    switch (key) {
      case 'c#': return 'csharp';
      case 't-sql': return 'sql';
      case 'cf': return 'coldfusion';
      default: return key;
    }
  }

  private updateUrl() {
    const queryParams: Record<string, string | number> = {};
    if (this.page() > 1) queryParams['page'] = this.page();
    if (this.search()) queryParams['search'] = this.search();
    if (this.group()) queryParams['group'] = this.group();
    this.router.navigate([], { queryParams, replaceUrl: false });
  }
}
