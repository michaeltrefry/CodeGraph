import { Component, Input, OnChanges, SimpleChanges, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import {
  SchemaCatalogResponse,
  SchemaForeignKey,
  SchemaObject,
  SchemaParameter,
  SchemaProcedure
} from '../../core/models';

type SchemaCatalogItem = SchemaObject | SchemaProcedure;

@Component({
  selector: 'app-schema-catalog',
  imports: [RouterLink],
  templateUrl: './schema-catalog.component.html',
  styleUrl: './schema-catalog.component.scss'
})
export class SchemaCatalogComponent implements OnChanges {
  private api = inject(ApiService);
  private requestId = 0;

  @Input({ required: true }) projectName = '';

  catalog = signal<SchemaCatalogResponse | null>(null);
  loading = signal(false);
  error = signal('');
  expanded = signal<Set<number>>(new Set());

  ngOnChanges(changes: SimpleChanges) {
    if (changes['projectName']) this.loadCatalog();
  }

  objects(): SchemaCatalogItem[] {
    const catalog = this.catalog();
    if (!catalog) return [];
    return [...catalog.tables, ...catalog.views, ...catalog.procedures];
  }

  isProcedure(item: SchemaCatalogItem): item is SchemaProcedure {
    return 'routineType' in item;
  }

  toggle(id: number) {
    this.expanded.update(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  isExpanded(id: number) {
    return this.expanded().has(id);
  }

  summary(item: SchemaCatalogItem) {
    if (this.isProcedure(item)) return `${item.parameters.length} parameters`;
    const parts = [`${item.columns.length} columns`];
    if (item.indexes.length) parts.push(`${item.indexes.length} indexes`);
    if (item.foreignKeys.length) parts.push(`${item.foreignKeys.length} foreign keys`);
    return parts.join(' · ');
  }

  formatForeignKey(foreignKey: SchemaForeignKey) {
    return `${foreignKey.columns.join(', ')} -> ${foreignKey.referencedTable} (${foreignKey.referencedColumns.join(', ') || '?'})`;
  }

  formatParameter(parameter: SchemaParameter) {
    return `${parameter.mode} ${parameter.dataType}${parameter.nullable ? '' : ' not null'}`;
  }

  private loadCatalog() {
    if (!this.projectName) return;
    const requestId = ++this.requestId;
    this.loading.set(true);
    this.error.set('');
    this.catalog.set(null);
    this.expanded.set(new Set());

    this.api.getSchemaCatalog(this.projectName).subscribe({
      next: catalog => {
        if (requestId !== this.requestId) return;
        this.catalog.set(catalog);
        this.loading.set(false);
      },
      error: () => {
        if (requestId !== this.requestId) return;
        this.error.set('Unable to load schema catalog.');
        this.loading.set(false);
      }
    });
  }
}
