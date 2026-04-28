import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  MemoryClaimBundle,
  MemoryClaimSeed,
  MemoryDiagnostics,
  MemoryEntity,
  MemoryEntityBundle,
  MemoryEntitySeed,
  MemoryGraphResponse,
  MemorySearchResult
} from '../../core/models';
import { MemoryDetailPanelComponent } from './memory-detail-panel.component';
import { MemoryGraphViewComponent } from './memory-graph-view.component';

@Component({
  selector: 'app-memory-page',
  standalone: true,
  imports: [DecimalPipe, MemoryDetailPanelComponent, MemoryGraphViewComponent],
  templateUrl: './memory-page.component.html',
  styleUrl: './memory-page.component.scss'
})
export class MemoryPageComponent implements OnInit {
  private api = inject(ApiService);

  loading = signal(true);
  loadingDetail = signal(false);
  loadingDiagnostics = signal(false);
  searching = signal(false);

  viewMode = signal<'graph' | 'list'>('graph');
  focusMode = signal(false);
  pageSize = signal(250);
  pageIndex = signal(0);
  searchText = signal('');

  graphData = signal<MemoryGraphResponse | null>(null);
  diagnostics = signal<MemoryDiagnostics | null>(null);
  searchResults = signal<MemorySearchResult | null>(null);
  selectedEntityId = signal<string | null>(null);
  selectedBundle = signal<MemoryEntityBundle | null>(null);
  selectedClaimBundle = signal<MemoryClaimBundle | null>(null);
  activeTypes = signal<Set<string>>(new Set());
  private typeFiltersInitialized = signal(false);

  readonly allNodes = computed(() => this.graphData()?.nodes ?? []);
  readonly allLinks = computed(() => this.graphData()?.links ?? []);
  readonly nodes = computed(() => {
    const active = this.activeTypes();
    return this.allNodes().filter(node => active.has(node.type));
  });
  readonly links = computed(() => {
    const visibleNodeIds = new Set(this.nodes().map(node => node.id));
    return this.allLinks().filter(link => visibleNodeIds.has(link.source) && visibleNodeIds.has(link.target));
  });
  readonly degreeByNode = computed(() => {
    const degrees = new Map<string, number>();
    for (const link of this.links()) {
      degrees.set(link.source, (degrees.get(link.source) ?? 0) + 1);
      degrees.set(link.target, (degrees.get(link.target) ?? 0) + 1);
    }
    return degrees;
  });
  readonly nodeLookup = computed(() => {
    const lookup = new Map<string, MemoryEntity>();
    for (const node of this.nodes()) {
      lookup.set(node.id, {
        id: node.id,
        label: node.label,
        type: node.type,
        summary: node.summary,
        aliases: [],
        source: node.source ?? '',
        createdAt: node.createdAt,
        updatedAt: node.updatedAt
      });
    }

    const selected = this.selectedBundle()?.entity;
    if (selected) {
      lookup.set(selected.id, selected);
    }

    return lookup;
  });
  readonly visibleNodes = computed(() =>
    [...this.nodes()].sort((a, b) => this.compareByRecencyAndLabel(a.updatedAt, b.updatedAt, a.label, b.label))
  );
  readonly hasPreviousPage = computed(() => !this.focusMode() && this.pageIndex() > 0);
  readonly hasNextPage = computed(() => {
    if (this.focusMode()) return false;
    const total = this.graphData()?.totalNodeCount ?? 0;
    return (this.pageIndex() + 1) * this.pageSize() < total;
  });
  readonly pageStart = computed(() => this.pageIndex() * this.pageSize() + 1);
  readonly pageEnd = computed(() => {
    const total = this.graphData()?.totalNodeCount ?? 0;
    return Math.min((this.pageIndex() + 1) * this.pageSize(), total);
  });
  readonly typeSummary = computed(() => {
    const counts = new Map<string, number>();
    for (const node of this.allNodes()) {
      counts.set(node.type, (counts.get(node.type) ?? 0) + 1);
    }

    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .slice(0, 8);
  });
  readonly searchEntityResults = computed(() => this.searchResults()?.entities ?? []);
  readonly searchClaimResults = computed(() => this.searchResults()?.claims ?? []);
  readonly neighborRows = computed(() => {
    const bundle = this.selectedBundle();
    if (!bundle) return [] as Array<{ id: string; label: string; type: string; edgeType: string; updatedAt?: string }>;

    return bundle.neighborEdges
      .map(edge => {
        const targetId = edge.fromEntityId === bundle.entity.id ? edge.toEntityId : edge.fromEntityId;
        const target = this.nodeLookup().get(targetId);
        return {
          id: targetId,
          label: target?.label ?? targetId,
          type: target?.type ?? 'unknown',
          edgeType: edge.edgeType,
          updatedAt: target?.updatedAt ?? edge.updatedAt
        };
      })
      .sort((a, b) => this.compareByRecencyAndLabel(a.updatedAt, b.updatedAt, a.label, b.label));
  });

  ngOnInit(): void {
    this.loadDiagnostics();
    this.loadOverview();
  }

  setViewMode(mode: 'graph' | 'list'): void {
    this.viewMode.set(mode);
  }

  toggleType(type: string): void {
    const current = this.activeTypes();
    const next = new Set(current);
    if (next.has(type)) {
      next.delete(type);
    } else {
      next.add(type);
    }
    this.activeTypes.set(next);
  }

  isTypeActive(type: string): boolean {
    return this.activeTypes().has(type);
  }

  updateSearchText(value: string): void {
    this.searchText.set(value);
  }

  runSearch(): void {
    const query = this.searchText().trim();
    if (!query) {
      this.searchResults.set(null);
      return;
    }

    this.searching.set(true);
    this.api.searchMemory(query, 8, 8).subscribe({
      next: result => {
        this.searchResults.set(result);
        this.searching.set(false);
      },
      error: () => this.searching.set(false)
    });
  }

  clearSearch(): void {
    this.searchText.set('');
    this.searchResults.set(null);
  }

  refreshOverview(): void {
    this.loadDiagnostics();
    this.loadOverview();
  }

  changePage(delta: number): void {
    if (this.focusMode()) return;
    this.pageIndex.update(current => Math.max(current + delta, 0));
    this.loadOverview(false);
  }

  changePageSize(size: number): void {
    this.pageSize.set(size);
    this.pageIndex.set(0);
    this.loadOverview();
  }

  focusSearchEntity(entity: MemoryEntitySeed): void {
    this.focusEntity(entity.entityId);
  }

  focusEntity(entityId: string, preserveClaimBundle = false): void {
    this.loading.set(true);
    this.loadingDetail.set(true);
    this.selectedEntityId.set(entityId);
    if (!preserveClaimBundle) {
      this.selectedClaimBundle.set(null);
    }

    forkJoin({
      graph: this.api.getMemoryEntityGraph(entityId, 500),
      bundle: this.api.getMemoryEntityBundle(entityId, {
        includeSuperseded: true,
        includeConflicts: true,
        neighborLimit: 500
      })
    }).subscribe({
      next: ({ graph, bundle }) => {
        this.syncTypeFilters(graph.nodes);
        this.graphData.set(graph);
        this.selectedBundle.set(bundle);
        this.focusMode.set(true);
        this.loading.set(false);
        this.loadingDetail.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadingDetail.set(false);
      }
    });
  }

  inspectClaim(claimId: string): void {
    this.api.getMemoryClaimBundle(claimId, {
      includeSupersessionChain: true,
      includeConflicts: true,
      includeEvidence: true
    }).subscribe({
      next: bundle => this.selectedClaimBundle.set(bundle)
    });
  }

  inspectSearchClaim(claim: MemoryClaimSeed): void {
    this.api.getMemoryClaimBundle(claim.claimId, {
      includeSupersessionChain: true,
      includeConflicts: true,
      includeEvidence: true
    }).subscribe({
      next: bundle => {
        this.selectedClaimBundle.set(bundle);
        this.focusEntity(bundle.claim.subjectEntityId, true);
      }
    });
  }

  clearSelection(): void {
    this.selectedEntityId.set(null);
    this.selectedBundle.set(null);
    this.selectedClaimBundle.set(null);
  }

  formatType(value: string): string {
    return value
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/\b\w/g, char => char.toUpperCase());
  }

  seedDisplayText(seed: MemoryEntitySeed | MemoryClaimSeed): string {
    if ('normalizedText' in seed) {
      return seed.normalizedText;
    }
    return seed.label;
  }

  seedMatchedFields(seed: MemoryEntitySeed | MemoryClaimSeed): string[] {
    return seed.diagnostics?.matchedFields ?? [];
  }

  formatNodeMeta(nodeId: string, updatedAt?: string): string {
    const parts = [`${this.degreeByNode().get(nodeId) ?? 0} visible links`];
    if (updatedAt) {
      parts.push(`updated ${new Date(updatedAt).toLocaleString()}`);
    }
    return parts.join(' | ');
  }

  isSelectedNode(nodeId: string): boolean {
    return this.selectedEntityId() === nodeId;
  }

  formatHealthSignal(signal: string): string {
    return this.formatType(signal);
  }

  loadOverview(clearDetail = true): void {
    this.loading.set(true);
    this.focusMode.set(false);

    if (clearDetail) {
      this.clearSelection();
    }

    this.api.getMemoryGraph(this.pageSize(), this.pageIndex() * this.pageSize()).subscribe({
      next: graph => {
        this.syncTypeFilters(graph.nodes);
        this.graphData.set(graph);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  private loadDiagnostics(): void {
    this.loadingDiagnostics.set(true);
    this.api.getMemoryDiagnostics(15, 8).subscribe({
      next: diagnostics => {
        this.diagnostics.set(diagnostics);
        this.loadingDiagnostics.set(false);
      },
      error: () => this.loadingDiagnostics.set(false)
    });
  }

  private syncTypeFilters(nodes: MemoryGraphResponse['nodes']): void {
    const types = [...new Set(nodes.map(node => node.type))].sort((a, b) => a.localeCompare(b));
    if (types.length === 0) {
      this.activeTypes.set(new Set());
      this.typeFiltersInitialized.set(false);
      return;
    }

    const current = this.activeTypes();
    if (!this.typeFiltersInitialized()) {
      this.activeTypes.set(new Set(types));
      this.typeFiltersInitialized.set(true);
      return;
    }

    const next = new Set<string>();
    for (const type of types) {
      if (current.has(type)) {
        next.add(type);
      }
    }

    this.activeTypes.set(next);
  }

  private compareByRecencyAndLabel(aDate: string | undefined, bDate: string | undefined, aLabel: string, bLabel: string): number {
    const aTime = aDate ? Date.parse(aDate) : 0;
    const bTime = bDate ? Date.parse(bDate) : 0;
    if (bTime !== aTime) {
      return bTime - aTime;
    }
    return aLabel.localeCompare(bLabel);
  }
}
