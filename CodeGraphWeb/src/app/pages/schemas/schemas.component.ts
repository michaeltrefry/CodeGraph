import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { SchemaListItem } from '../../core/models';
import { TypeaheadComponent, TypeaheadItem } from '../../shared/typeahead.component';

@Component({
  selector: 'app-schemas',
  imports: [FormsModule, RouterLink, TypeaheadComponent],
  templateUrl: './schemas.component.html',
  styleUrl: './schemas.component.scss'
})
export class SchemasComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  search = signal('');
  server = signal('');
  database = signal('');
  page = signal(1);
  pageSize = 25;
  items = signal<SchemaListItem[]>([]);
  total = signal(0);
  totalTables = signal(0);
  totalViews = signal(0);
  totalProcedures = signal(0);
  servers = signal<string[]>([]);
  databases = signal<string[]>([]);
  loading = signal(false);

  databaseItems = computed<TypeaheadItem[]>(() =>
    this.databases().map(value => ({ value, label: value }))
  );

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit() {
    const params = this.route.snapshot.queryParamMap;
    const page = Number(params.get('page'));
    if (page > 0) this.page.set(page);
    this.search.set(params.get('search') ?? '');
    this.server.set(params.get('server') ?? '');
    this.database.set(params.get('database') ?? '');
    this.load();
  }

  onSearchInput(value: string) {
    this.search.set(value);
    this.page.set(1);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.load(), 300);
  }

  setServer(value: string) {
    this.server.set(value);
    this.database.set('');
    this.page.set(1);
    this.load();
  }

  setDatabase(value: string) {
    this.database.set(value);
    this.page.set(1);
    this.load();
  }

  load() {
    this.loading.set(true);
    this.updateUrl();
    this.api.listSchemas(
      this.search() || undefined,
      this.server() || undefined,
      this.database() || undefined,
      this.page(),
      this.pageSize
    ).subscribe({
      next: response => {
        this.items.set(response.items);
        this.total.set(response.total);
        this.totalTables.set(response.totalTables);
        this.totalViews.set(response.totalViews);
        this.totalProcedures.set(response.totalProcedures);
        this.servers.set(response.servers);
        this.databases.set(response.databases);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  totalPages() {
    return Math.max(1, Math.ceil(this.total() / this.pageSize));
  }

  prevPage() {
    if (this.page() > 1) {
      this.page.update(value => value - 1);
      this.load();
    }
  }

  nextPage() {
    if (this.page() < this.totalPages()) {
      this.page.update(value => value + 1);
      this.load();
    }
  }

  formatDate(value?: string) {
    return value ? new Date(value).toLocaleDateString() : '-';
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

  private updateUrl() {
    const queryParams: Record<string, string | number> = {};
    if (this.page() > 1) queryParams['page'] = this.page();
    if (this.search()) queryParams['search'] = this.search();
    if (this.server()) queryParams['server'] = this.server();
    if (this.database()) queryParams['database'] = this.database();
    this.router.navigate([], { queryParams, replaceUrl: false });
  }
}
