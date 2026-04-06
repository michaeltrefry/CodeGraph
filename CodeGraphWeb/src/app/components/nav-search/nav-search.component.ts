import { Component, inject, signal, ElementRef, ViewChild, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subject, debounceTime, distinctUntilChanged, switchMap, of, takeUntil, filter } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { SearchResultItem, LABEL_ICONS } from '../../core/models';

@Component({
  selector: 'app-nav-search',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './nav-search.component.html',
  styleUrl: './nav-search.component.scss'
})
export class NavSearchComponent implements OnDestroy {
  private api = inject(ApiService);
  private router = inject(Router);
  private destroy$ = new Subject<void>();

  query = signal('');
  results = signal<SearchResultItem[]>([]);
  totalResults = signal(0);
  showDropdown = signal(false);
  activeIndex = signal(-1);
  loading = signal(false);

  private search$ = new Subject<string>();

  @ViewChild('searchInput') searchInput!: ElementRef<HTMLInputElement>;

  constructor() {
    this.search$.pipe(
      debounceTime(250),
      distinctUntilChanged(),
      switchMap(q => {
        if (q.trim().length < 2) {
          this.loading.set(false);
          return of(null);
        }
        this.loading.set(true);
        return this.api.search(q, 1, 25);
      }),
      takeUntil(this.destroy$)
    ).subscribe(res => {
      this.loading.set(false);
      if (res) {
        this.results.set(res.items);
        this.totalResults.set(res.total);
        this.showDropdown.set(res.items.length > 0);
        this.activeIndex.set(-1);
      } else {
        this.results.set([]);
        this.totalResults.set(0);
        this.showDropdown.set(false);
      }
    });
  }

  onInput(value: string) {
    this.query.set(value);
    this.search$.next(value);
  }

  onKeydown(event: KeyboardEvent) {
    const items = this.results();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.activeIndex.set(Math.min(this.activeIndex() + 1, items.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.activeIndex.set(Math.max(this.activeIndex() - 1, -1));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (this.activeIndex() >= 0 && this.activeIndex() < items.length) {
        this.selectItem(items[this.activeIndex()]);
      } else {
        this.submitSearch();
      }
    } else if (event.key === 'Escape') {
      this.showDropdown.set(false);
      this.searchInput.nativeElement.blur();
    }
  }

  selectItem(item: SearchResultItem) {
    this.showDropdown.set(false);
    this.query.set('');
    this.navigateToItem(item);
  }

  submitSearch() {
    const q = this.query().trim();
    if (!q) return;
    this.showDropdown.set(false);

    // If exactly one result, navigate directly
    if (this.totalResults() === 1 && this.results().length === 1) {
      this.navigateToItem(this.results()[0]);
      this.query.set('');
      return;
    }

    this.router.navigate(['/search'], { queryParams: { q } });
    this.query.set('');
  }

  onBlur() {
    // Delay to allow click on dropdown items
    setTimeout(() => this.showDropdown.set(false), 200);
  }

  onFocus() {
    if (this.results().length > 0 && this.query().trim().length >= 2) {
      this.showDropdown.set(true);
    }
  }

  getIcon(item: SearchResultItem): string {
    if (item.type === 'repository') return '📦';
    return (item.nodeLabel && LABEL_ICONS[item.nodeLabel]) || '🔹';
  }

  getTypeLabel(item: SearchResultItem): string {
    if (item.type === 'repository') return 'Repo';
    return item.nodeLabel || 'Node';
  }

  private navigateToItem(item: SearchResultItem) {
    if (item.type === 'repository') {
      this.router.navigate(['/repos', item.name]);
    } else if (item.nodeId) {
      this.router.navigate(['/nodes', item.nodeId]);
    }
  }

  ngOnDestroy() {
    this.destroy$.next();
    this.destroy$.complete();
  }
}
