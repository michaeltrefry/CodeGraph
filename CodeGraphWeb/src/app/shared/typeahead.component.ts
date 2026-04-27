import {
  Component, Input, Output, EventEmitter, signal, ElementRef,
  ViewChild, OnChanges, SimpleChanges, OnDestroy, forwardRef
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Observable, Subject, Subscription, debounceTime, switchMap, of, catchError } from 'rxjs';

export interface TypeaheadItem {
  value: string;
  label: string;
  description?: string;
  icon?: string;
}

@Component({
  selector: 'app-typeahead',
  standalone: true,
  providers: [{
    provide: NG_VALUE_ACCESSOR,
    useExisting: forwardRef(() => TypeaheadComponent),
    multi: true
  }],
  template: `
    <div class="typeahead-container" [class.typeahead-open]="showDropdown()">
      <input
        #input
        type="text"
        [placeholder]="placeholder"
        [value]="query()"
        (input)="onInput(input.value)"
        (keydown)="onKeydown($event)"
        (focus)="onFocus()"
        (blur)="onBlur()"
        [class]="inputClass"
      />
      @if (loading()) {
        <span class="typeahead-spinner"></span>
      }
      @if (showDropdown()) {
        <div class="typeahead-dropdown" [class]="dropdownClass">
          @if (showAllOption && !query()) {
            <div class="typeahead-item" [class.active]="activeIndex() === -1"
                 (mousedown)="selectAll()">
              <span class="typeahead-item-label">{{ allLabel }}</span>
            </div>
          }
          @for (item of filteredItems(); track item.value; let i = $index) {
            <div class="typeahead-item"
                 [class.active]="i === activeIndex()"
                 (mousedown)="selectItem(item)">
              @if (item.icon) {
                <span class="typeahead-item-icon">{{ item.icon }}</span>
              }
              <div class="typeahead-item-content">
                <span class="typeahead-item-label">{{ item.label }}</span>
                @if (item.description) {
                  <span class="typeahead-item-desc">{{ item.description }}</span>
                }
              </div>
            </div>
          }
          @if (filteredItems().length === 0 && query().length >= minChars) {
            <div class="typeahead-empty">No matches found</div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .typeahead-container {
      position: relative;
      display: inline-block;
      width: 100%;
    }

    input {
      width: 100%;
      box-sizing: border-box;
      padding: 8px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      font-size: var(--fs-sm);
      color: var(--text);
      background: var(--surface);
      font-family: inherit;

      &:focus {
        outline: none;
        border-color: var(--accent-dim);
        background: var(--surface-2);
        box-shadow: 0 0 0 2px var(--accent-weak);
      }

      &::placeholder {
        color: var(--faint);
      }
    }

    .typeahead-spinner {
      position: absolute;
      right: 10px;
      top: 50%;
      transform: translateY(-50%);
      width: 14px;
      height: 14px;
      border: 2px solid var(--border);
      border-top-color: var(--accent);
      border-radius: 50%;
      animation: typeahead-spin 0.6s linear infinite;
    }

    @keyframes typeahead-spin {
      to { transform: translateY(-50%) rotate(360deg); }
    }

    .typeahead-dropdown {
      position: absolute;
      top: calc(100% + 4px);
      left: 0;
      right: 0;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      box-shadow: var(--shadow-lg);
      z-index: 100;
      max-height: 280px;
      overflow-y: auto;
    }

    .typeahead-item {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      cursor: pointer;
      font-size: var(--fs-sm);
      color: var(--text);
      transition: background var(--transition), color var(--transition);

      &:hover, &.active {
        background: var(--accent-weak);
        color: var(--text);
      }
    }

    .typeahead-item-icon {
      font-size: var(--fs-sm);
      flex-shrink: 0;
    }

    .typeahead-item-content {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }

    .typeahead-item-label {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .typeahead-item-desc {
      font-size: var(--fs-xs);
      color: var(--muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .typeahead-empty {
      padding: 10px 12px;
      font-size: var(--fs-sm);
      color: var(--muted);
      text-align: center;
    }
  `]
})
export class TypeaheadComponent implements ControlValueAccessor, OnChanges, OnDestroy {
  /** Static list of items to filter locally */
  @Input() items: TypeaheadItem[] = [];

  /** Async search function - called with query string, returns Observable<TypeaheadItem[]> */
  @Input() searchFn?: (query: string) => Observable<TypeaheadItem[]>;

  @Input() placeholder = '';
  @Input() minChars = 1;
  @Input() debounceMs = 250;
  @Input() inputClass = '';
  @Input() dropdownClass = '';

  /** Show an "All" option at the top when query is empty */
  @Input() showAllOption = false;
  @Input() allLabel = 'All';

  /** Emitted when a value is selected from the dropdown */
  @Output() selected = new EventEmitter<string>();

  /** Emitted when the "All" option is selected */
  @Output() allSelected = new EventEmitter<void>();

  query = signal('');
  filteredItems = signal<TypeaheadItem[]>([]);
  showDropdown = signal(false);
  activeIndex = signal(-1);
  loading = signal(false);

  @ViewChild('input') inputRef!: ElementRef<HTMLInputElement>;

  private search$ = new Subject<string>();
  private searchSub?: Subscription;

  private onChange: (value: string) => void = () => {};
  private onTouched: () => void = () => {};

  constructor() {
    this.searchSub = this.search$.pipe(
      debounceTime(this.debounceMs),
      switchMap(q => {
        if (this.searchFn && q.length >= this.minChars) {
          this.loading.set(true);
          return this.searchFn(q).pipe(catchError(() => of([])));
        }
        this.loading.set(false);
        return of(this.filterStatic(q));
      })
    ).subscribe(results => {
      this.loading.set(false);
      this.filteredItems.set(results);
      this.activeIndex.set(-1);
      this.showDropdown.set(results.length > 0 || (this.showAllOption && !this.query()));
    });
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['items'] && !this.searchFn) {
      this.filteredItems.set(this.filterStatic(this.query()));
    }
  }

  writeValue(value: string): void {
    this.query.set(value || '');
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  onInput(value: string) {
    this.query.set(value);
    this.onChange(value);

    if (this.searchFn) {
      if (value.length >= this.minChars) {
        this.search$.next(value);
      } else {
        this.filteredItems.set([]);
        this.showDropdown.set(this.showAllOption && !value);
      }
    } else {
      // Static filtering - no debounce needed
      const filtered = this.filterStatic(value);
      this.filteredItems.set(filtered);
      this.activeIndex.set(-1);
      this.showDropdown.set(filtered.length > 0 || (this.showAllOption && !value));
    }
  }

  onFocus() {
    this.onTouched();
    if (this.searchFn) {
      if (this.query().length >= this.minChars && this.filteredItems().length > 0) {
        this.showDropdown.set(true);
      }
    } else {
      // Show all static items on focus
      const filtered = this.filterStatic(this.query());
      this.filteredItems.set(filtered);
      this.showDropdown.set(filtered.length > 0 || this.showAllOption);
    }
  }

  onBlur() {
    setTimeout(() => this.showDropdown.set(false), 200);
  }

  onKeydown(event: KeyboardEvent) {
    const items = this.filteredItems();
    const hasAll = this.showAllOption && !this.query();
    const totalItems = items.length + (hasAll ? 1 : 0);

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      const min = hasAll ? -1 : 0;
      this.activeIndex.set(Math.min(this.activeIndex() + 1, items.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      const min = hasAll ? -1 : 0;
      this.activeIndex.set(Math.max(this.activeIndex() - 1, min));
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (hasAll && this.activeIndex() === -1) {
        this.selectAll();
      } else if (this.activeIndex() >= 0 && this.activeIndex() < items.length) {
        this.selectItem(items[this.activeIndex()]);
      }
    } else if (event.key === 'Escape') {
      this.showDropdown.set(false);
      this.inputRef.nativeElement.blur();
    }
  }

  selectItem(item: TypeaheadItem) {
    this.query.set(item.label);
    this.onChange(item.value);
    this.showDropdown.set(false);
    this.selected.emit(item.value);
  }

  selectAll() {
    this.query.set('');
    this.onChange('');
    this.showDropdown.set(false);
    this.allSelected.emit();
  }

  private filterStatic(q: string): TypeaheadItem[] {
    if (!q) return this.items;
    const lower = q.toLowerCase();
    return this.items.filter(item =>
      item.label.toLowerCase().includes(lower) ||
      item.value.toLowerCase().includes(lower)
    );
  }

  ngOnDestroy() {
    this.searchSub?.unsubscribe();
    this.search$.complete();
  }
}
