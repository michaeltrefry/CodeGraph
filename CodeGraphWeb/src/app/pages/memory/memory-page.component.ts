import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  computed,
  effect,
  inject,
  signal
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import * as d3 from 'd3';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  MemoryClaim,
  MemoryClaimBundle,
  MemoryClaimSeed,
  MemoryEntity,
  MemoryEntityBundle,
  MemoryEntitySeed,
  MemoryGraphLink,
  MemoryGraphNode,
  MemoryGraphResponse,
  MemorySearchResult
} from '../../core/models';

interface SimNode extends d3.SimulationNodeDatum {
  id: string;
  label: string;
  type: string;
  summary: string;
  degree: number;
  radius: number;
}

interface SimLink extends d3.SimulationLinkDatum<SimNode> {
  relationship: string;
}

@Component({
  selector: 'app-memory-page',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  templateUrl: './memory-page.component.html',
  styleUrl: './memory-page.component.scss'
})
export class MemoryPageComponent implements AfterViewInit, OnDestroy {
  @ViewChild('svg', { static: false }) svgRef?: ElementRef<SVGSVGElement>;
  @ViewChild('wrapper', { static: false }) wrapperRef?: ElementRef<HTMLDivElement>;

  private api = inject(ApiService);

  loading = signal(true);
  loadingDetail = signal(false);
  searching = signal(false);

  viewMode = signal<'graph' | 'list'>('graph');
  focusMode = signal(false);
  pageSize = signal(250);
  pageIndex = signal(0);
  searchText = signal('');

  graphData = signal<MemoryGraphResponse | null>(null);
  searchResults = signal<MemorySearchResult | null>(null);
  selectedEntityId = signal<string | null>(null);
  selectedBundle = signal<MemoryEntityBundle | null>(null);
  selectedClaimBundle = signal<MemoryClaimBundle | null>(null);
  activeTypes = signal<Set<string>>(new Set());
  private typeFiltersInitialized = signal(false);

  private simulation?: d3.Simulation<SimNode, SimLink>;
  private resizeObserver?: ResizeObserver;
  private colorScale = d3.scaleOrdinal<string, string>(d3.schemeTableau10);

  readonly allNodes = computed(() => this.graphData()?.nodes ?? []);
  readonly allLinks = computed(() => this.graphData()?.links ?? []);
  readonly availableTypes = computed(() =>
    [...new Set(this.allNodes().map(node => node.type))]
      .sort((a, b) => a.localeCompare(b))
  );

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
    const lookup = new Map<string, MemoryGraphNode>();
    for (const node of this.nodes()) {
      lookup.set(node.id, node);
    }

    const selected = this.selectedBundle()?.entity;
    if (selected) {
      lookup.set(selected.id, {
        id: selected.id,
        label: selected.label,
        type: selected.type,
        summary: selected.summary,
        source: selected.source,
        createdAt: selected.createdAt,
        updatedAt: selected.updatedAt
      });
    }

    return lookup;
  });

  readonly visibleNodes = computed(() =>
    [...this.nodes()].sort((a, b) =>
      new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime() ||
      a.label.localeCompare(b.label))
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
    if (!bundle) return [] as Array<{ id: string; label: string; type: string; edgeType: string; updatedAt: string }>;

    return bundle.neighborEdges
      .map(edge => {
        const targetId = edge.fromEntityId === bundle.entity.id ? edge.toEntityId : edge.fromEntityId;
        const target = this.nodeLookup().get(targetId);
        return {
          id: targetId,
          label: target?.label ?? targetId,
          type: target?.type ?? 'unknown',
          edgeType: edge.edgeType,
          updatedAt: edge.updatedAt
        };
      })
      .sort((a, b) =>
        new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime() ||
        a.label.localeCompare(b.label));
  });

  readonly renderGraphEffect = effect(() => {
    const graph = this.graphData();
    const filteredNodes = this.nodes();
    const filteredLinks = this.links();
    const focusedEntityId = this.focusMode() ? this.selectedEntityId() : null;
    if (!graph || this.loading() || this.viewMode() !== 'graph') return;
    setTimeout(() => this.buildGraph({
      ...graph,
      nodes: filteredNodes,
      links: filteredLinks
    }, focusedEntityId), 0);
  });

  ngAfterViewInit(): void {
    this.loadOverview();
  }

  ngOnDestroy(): void {
    this.simulation?.stop();
    this.resizeObserver?.disconnect();
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
    this.loadOverview();
  }

  resetToOverview(): void {
    this.pageIndex.set(0);
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

  focusEntity(entityId: string): void {
    this.loading.set(true);
    this.loadingDetail.set(true);
    this.selectedEntityId.set(entityId);
    this.selectedClaimBundle.set(null);

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
        this.focusEntity(bundle.claim.subjectEntityId);
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

  formatClaim(claim: MemoryClaim): string {
    const subject = this.entityName(claim.subjectEntityId);
    const object = claim.objectEntityId ? this.entityName(claim.objectEntityId) : null;
    const value = claim.valueText?.trim();
    return [subject, claim.predicate, object ?? value].filter(Boolean).join(' ');
  }

  entityName(entityId: string): string {
    return this.nodeLookup().get(entityId)?.label
      ?? (this.selectedBundle()?.entity.id === entityId ? this.selectedBundle()!.entity.label : entityId);
  }

  scoreBreakdown(seed: MemoryEntitySeed | MemoryClaimSeed): string {
    return Object.entries(seed.diagnostics.scoreBreakdown ?? {})
      .map(([key, value]) => `${key}: ${value.toFixed(2)}`)
      .join(', ');
  }

  isSelectedNode(nodeId: string): boolean {
    return this.selectedEntityId() === nodeId;
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

  private syncTypeFilters(nodes: MemoryGraphNode[]): void {
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

  private buildGraph(data: MemoryGraphResponse, focusedEntityId: string | null): void {
    if (!this.svgRef || !this.wrapperRef) return;

    const svgElement = this.svgRef.nativeElement;
    const wrapper = this.wrapperRef.nativeElement;
    const svg = d3.select(svgElement);
    svg.selectAll('*').remove();

    const width = wrapper.clientWidth;
    const height = wrapper.clientHeight || 620;
    svg.attr('viewBox', `0 0 ${width} ${height}`);

    this.resizeObserver?.disconnect();
    this.resizeObserver = new ResizeObserver(entries => {
      const { width: nextWidth, height: nextHeight } = entries[0].contentRect;
      if (nextWidth > 0 && nextHeight > 0) {
        svg.attr('viewBox', `0 0 ${nextWidth} ${nextHeight}`);
        this.simulation?.force('center', d3.forceCenter(nextWidth / 2, nextHeight / 2));
        this.simulation?.alpha(0.15).restart();
      }
    });
    this.resizeObserver.observe(wrapper);

    const degrees = new Map<string, number>();
    for (const link of data.links) {
      degrees.set(link.source, (degrees.get(link.source) ?? 0) + 1);
      degrees.set(link.target, (degrees.get(link.target) ?? 0) + 1);
    }

    const radiusScale = d3.scaleSqrt()
      .domain([0, d3.max([...degrees.values()]) ?? 1])
      .range([5, focusedEntityId ? 16 : 10]);

    const nodes: SimNode[] = data.nodes.map(node => ({
      id: node.id,
      label: node.label,
      type: node.type,
      summary: node.summary,
      degree: degrees.get(node.id) ?? 0,
      radius: node.id === focusedEntityId ? 20 : radiusScale(degrees.get(node.id) ?? 0)
    }));

    const nodeMap = new Map(nodes.map(node => [node.id, node]));
    const updatedAtById = new Map(data.nodes.map(node => [node.id, node.updatedAt]));
    const links: SimLink[] = data.links
      .filter(link => nodeMap.has(link.source) && nodeMap.has(link.target))
      .map(link => ({
        source: link.source,
        target: link.target,
        relationship: link.relationship
      }));

    if (nodes.length === 0) {
      svg.append('text')
        .attr('x', width / 2)
        .attr('y', height / 2)
        .attr('text-anchor', 'middle')
        .attr('fill', '#9ca3af')
        .text('No memory nodes to display.');
      return;
    }

    const showLabels = !!focusedEntityId || nodes.length <= 80;
    const labeledNodeIds = new Set<string>();
    if (!showLabels) {
      const topByDegree = [...nodes]
        .sort((a, b) => b.degree - a.degree || a.label.localeCompare(b.label))
        .slice(0, 16);

      const topByRecency = [...nodes]
        .sort((a, b) =>
          new Date(updatedAtById.get(b.id) ?? 0).getTime() -
          new Date(updatedAtById.get(a.id) ?? 0).getTime() ||
          a.label.localeCompare(b.label))
        .slice(0, 16);

      for (const node of [...topByDegree, ...topByRecency]) {
        labeledNodeIds.add(node.id);
      }
    }
    const g = svg.append('g');
    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.2, 6])
      .on('zoom', event => g.attr('transform', event.transform));
    svg.call(zoom);

    const linkSelection = g.append('g')
      .attr('stroke', '#d1d5db')
      .attr('stroke-opacity', 0.5)
      .selectAll('line')
      .data(links)
      .join('line')
      .attr('stroke-width', focusedEntityId ? 1.6 : 1.1);

    const nodeSelection = g.append('g')
      .selectAll<SVGGElement, SimNode>('g')
      .data(nodes)
      .join('g')
      .attr('cursor', 'pointer');

    const isLabelVisible = (node: SimNode) => showLabels || labeledNodeIds.has(node.id);

    const labelSelection = nodeSelection.append('text')
      .text(node => node.label)
      .attr('display', node => isLabelVisible(node) ? null : 'none')
      .attr('dy', node => node.radius + 12)
      .attr('text-anchor', 'middle')
      .attr('font-size', focusedEntityId ? '11px' : '10px')
      .attr('fill', '#374151')
      .attr('pointer-events', 'none');

    nodeSelection
      .on('mouseenter', (_event, node) => {
        if (isLabelVisible(node)) return;
        labelSelection
          .filter(candidate => candidate.id === node.id)
          .attr('display', null);
      })
      .on('mouseleave', (_event, node) => {
        if (isLabelVisible(node)) return;
        labelSelection
          .filter(candidate => candidate.id === node.id)
          .attr('display', 'none');
      })
      .on('click', (_event, node) => this.focusEntity(node.id))
      .call(d3.drag<SVGGElement, SimNode>()
        .on('start', (event, node) => {
          if (!event.active) this.simulation!.alphaTarget(0.3).restart();
          node.fx = node.x;
          node.fy = node.y;
        })
        .on('drag', (event, node) => {
          node.fx = event.x;
          node.fy = event.y;
        })
        .on('end', (event, node) => {
          if (!event.active) this.simulation!.alphaTarget(0);
          node.fx = null;
          node.fy = null;
        }));

    nodeSelection.append('circle')
      .attr('r', node => node.radius)
      .attr('fill', node => this.colorScale(node.type))
      .attr('stroke', node => node.id === focusedEntityId ? '#111827' : '#ffffff')
      .attr('stroke-width', node => node.id === focusedEntityId ? 2.5 : 1.5)
      .attr('opacity', node => focusedEntityId && node.id !== focusedEntityId ? 0.9 : 1);

    nodeSelection.append('title')
      .text(node => `${node.label}\n${this.formatType(node.type)}\n${node.degree} visible links${node.summary ? `\n\n${node.summary}` : ''}`);

    this.simulation?.stop();
    this.simulation = d3.forceSimulation<SimNode>(nodes)
      .force('link', d3.forceLink<SimNode, SimLink>(links)
        .id(node => node.id)
        .distance(focusedEntityId ? 110 : 55))
      .force('charge', d3.forceManyBody().strength(focusedEntityId ? -360 : -80))
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('collision', d3.forceCollide<SimNode>().radius(node => node.radius + (focusedEntityId ? 18 : 6)))
      .force('focusX', d3.forceX<SimNode>(node => node.id === focusedEntityId ? width / 2 : width / 2)
        .strength(node => node.id === focusedEntityId ? 0.45 : 0.03))
      .force('focusY', d3.forceY<SimNode>(node => node.id === focusedEntityId ? height / 2 : height / 2)
        .strength(node => node.id === focusedEntityId ? 0.45 : 0.03))
      .on('tick', () => {
        linkSelection
          .attr('x1', link => (link.source as SimNode).x ?? 0)
          .attr('y1', link => (link.source as SimNode).y ?? 0)
          .attr('x2', link => (link.target as SimNode).x ?? 0)
          .attr('y2', link => (link.target as SimNode).y ?? 0);

        nodeSelection.attr('transform', node => `translate(${node.x ?? 0},${node.y ?? 0})`);
      });

    const focusedNode = focusedEntityId ? nodeMap.get(focusedEntityId) : null;
    if (focusedNode) {
      focusedNode.fx = width / 2;
      focusedNode.fy = height / 2;
      this.simulation.alpha(0.7).restart();
    }
  }
}
