import { Component, Input, OnChanges, SimpleChanges, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import {
  SchemaCatalogResponse,
  SchemaConstraint,
  SchemaForeignKey,
  SchemaIndex,
  SchemaObject,
  SchemaParameter,
  SchemaProcedure
} from '../../core/models';

type SchemaCatalogItem = SchemaObject | SchemaProcedure;
type CatalogGroup = {
  key: 'tables' | 'views' | 'procedures';
  label: string;
  items: SchemaCatalogItem[];
};

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
  loadError = signal('');
  expandedGroups = signal<Set<string>>(new Set(['Tables', 'Views', 'Procedures']));
  expandedObjects = signal<Set<number>>(new Set());

  groups = computed<CatalogGroup[]>(() => {
    const catalog = this.catalog();
    if (!catalog) return [];

    return [
      { key: 'tables', label: 'Tables', items: catalog.tables },
      { key: 'views', label: 'Views', items: catalog.views },
      { key: 'procedures', label: 'Procedures', items: catalog.procedures }
    ];
  });

  totalObjects = computed(() => {
    const catalog = this.catalog();
    if (!catalog) return 0;
    return catalog.tables.length + catalog.views.length + catalog.procedures.length;
  });

  ngOnChanges(changes: SimpleChanges) {
    if (changes['projectName']) this.loadCatalog();
  }

  isProcedure(item: SchemaCatalogItem): item is SchemaProcedure {
    return 'routineType' in item;
  }

  toggleGroup(label: string) {
    this.expandedGroups.update(current => {
      const next = new Set(current);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  }

  isGroupExpanded(label: string) {
    return this.expandedGroups().has(label);
  }

  toggleObject(id: number) {
    this.expandedObjects.update(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  isObjectExpanded(id: number) {
    return this.expandedObjects().has(id);
  }

  objectSummary(item: SchemaCatalogItem) {
    if (this.isProcedure(item)) {
      const parameterLabel = item.parameters.length === 1 ? 'parameter' : 'parameters';
      return `${item.parameters.length} ${parameterLabel}`;
    }

    const parts = [`${item.columns.length} columns`];
    if (item.indexes.length) parts.push(`${item.indexes.length} indexes`);
    if (item.foreignKeys.length) parts.push(`${item.foreignKeys.length} foreign keys`);
    if (this.constraints(item).length) parts.push(`${this.constraints(item).length} constraints`);
    return parts.join(' · ');
  }

  constraints(item: SchemaObject) {
    return item.constraints ?? [];
  }

  formatNullable(nullable: boolean) {
    return nullable ? 'YES' : 'NO';
  }

  formatColumns(columns?: string[] | null) {
    return columns?.length ? columns.join(', ') : '-';
  }

  formatIndex(index: SchemaIndex) {
    const uniqueness = index.isUnique ? 'unique' : 'non-unique';
    const type = index.indexType ? ` · ${index.indexType}` : '';
    return `${uniqueness}${type}`;
  }

  formatForeignKey(foreignKey: SchemaForeignKey) {
    return `${this.formatColumns(foreignKey.columns)} -> ${foreignKey.referencedTable} (${this.formatColumns(foreignKey.referencedColumns)})`;
  }

  formatConstraint(constraint: SchemaConstraint) {
    const parts = [constraint.constraintType];
    if (constraint.columns.length) parts.push(this.formatColumns(constraint.columns));
    if (constraint.checkClause) parts.push(constraint.checkClause);
    return parts.join(' · ');
  }

  formatParameter(parameter: SchemaParameter) {
    return `${parameter.mode} ${parameter.dataType}${parameter.nullable ? '' : ' not null'}`;
  }

  private loadCatalog() {
    if (!this.projectName) {
      this.catalog.set(null);
      return;
    }

    const requestId = ++this.requestId;
    this.loading.set(true);
    this.loadError.set('');
    this.catalog.set(null);
    this.expandedObjects.set(new Set());

    this.api.getSchemaCatalog(this.projectName).subscribe({
      next: catalog => {
        if (requestId !== this.requestId) return;
        this.catalog.set(catalog);
        this.loading.set(false);
      },
      error: () => {
        if (requestId !== this.requestId) return;
        this.loadError.set('Unable to load schema catalog.');
        this.loading.set(false);
      }
    });
  }
}
