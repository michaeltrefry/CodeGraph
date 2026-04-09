import {
  Component, ElementRef, ViewChild, AfterViewInit,
  OnDestroy, inject, signal, computed, effect
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { Router } from '@angular/router';
import * as d3 from 'd3';
import { ApiService } from '../../core/api.service';
import {
  ClusterGraphNode, ClusterGraphEdge, ClusterInfo,
  ClusterDetailResponse, ClusterGraphResponse
} from '../../core/models';

interface SimNode extends d3.SimulationNodeDatum {
  id: string;
  clusterId: number | null;
  isFoundational: boolean;
  edgeCount: number;
  betweenness: number;
  radius: number;
}

interface SimLink extends d3.SimulationLinkDatum<SimNode> {
  count: number;
  isCrossCluster: boolean;
}

@Component({
  selector: 'app-clusters-page',
  standalone: true,
  imports: [DecimalPipe],
  templateUrl: './clusters-page.component.html',
  styleUrl: './clusters-page.component.scss'
})
export class ClustersPageComponent implements AfterViewInit, OnDestroy {
  @ViewChild('svg', { static: false }) svgRef?: ElementRef<SVGSVGElement>;
  @ViewChild('wrapper', { static: false }) wrapperRef?: ElementRef<HTMLDivElement>;

  private api = inject(ApiService);
  private router = inject(Router);

  loading = signal(true);
  viewMode = signal<'graph' | 'list'>('graph');
  graphData = signal<ClusterGraphResponse | null>(null);
  selectedClusterId = signal<number | null>(null);
  clusterDetail = signal<ClusterDetailResponse | null>(null);
  loadingDetail = signal(false);

  clusters = computed(() => this.graphData()?.clusters ?? []);
  modularity = computed(() => this.graphData()?.modularity ?? 0);

  /** All distinct edge types found in the graph data */
  availableEdgeTypes = computed(() => {
    const data = this.graphData();
    if (!data) return [] as string[];
    const types = new Set<string>();
    for (const e of data.edges) {
      for (const t of Object.keys(e.typeCounts)) {
        types.add(t);
      }
    }
    return [...types].sort();
  });

  /** Set of edge types currently enabled (all on by default) */
  enabledEdgeTypes = signal<Set<string>>(new Set());
  filterPanelOpen = signal(true);

  /** Graph data filtered to only include edges matching enabled types */
  filteredGraphData = computed(() => {
    const data = this.graphData();
    if (!data) return null;
    const enabled = this.enabledEdgeTypes();

    const filteredEdges = data.edges
      .map(e => {
        const filteredCounts: Record<string, number> = {};
        let total = 0;
        for (const [type, count] of Object.entries(e.typeCounts)) {
          if (enabled.has(type)) {
            filteredCounts[type] = count;
            total += count;
          }
        }
        if (total === 0) return null;
        return { ...e, typeCounts: filteredCounts, count: total };
      })
      .filter((e): e is ClusterGraphEdge => e !== null);

    return { ...data, edges: filteredEdges };
  });

  /** Map of clusterId → repo name with most external (cross-cluster) edges */
  private clusterNameMap = computed(() => {
    const data = this.graphData();
    if (!data) return new Map<number, string>();

    // Count external edges per node
    const extCounts = new Map<string, number>();
    for (const e of data.edges) {
      if (e.isCrossCluster) {
        extCounts.set(e.source, (extCounts.get(e.source) ?? 0) + e.count);
        extCounts.set(e.target, (extCounts.get(e.target) ?? 0) + e.count);
      }
    }

    // Group nodes by cluster, find the one with most external edges
    const bestPerCluster = new Map<number, string>();
    const bestCount = new Map<number, number>();
    for (const n of data.nodes) {
      if (n.clusterId == null) continue;
      const ext = extCounts.get(n.name) ?? 0;
      if (ext > (bestCount.get(n.clusterId) ?? 0)) {
        bestCount.set(n.clusterId, ext);
        bestPerCluster.set(n.clusterId, n.name);
      }
    }
    return bestPerCluster;
  });

  private simulation?: d3.Simulation<SimNode, SimLink>;
  private resizeObserver?: ResizeObserver;
  private colorScale = d3.scaleOrdinal(d3.schemeTableau10);

  // D3 selection references for highlight updates
  private nodeSelection?: d3.Selection<SVGGElement, SimNode, SVGGElement, unknown>;
  private linkSelection?: d3.Selection<SVGLineElement, SimLink, SVGGElement, unknown>;

  private filterEffect = effect(() => {
    const filtered = this.filteredGraphData();
    // Only rebuild if we have data and are in graph mode
    if (filtered && this.viewMode() === 'graph' && !this.loading()) {
      setTimeout(() => this.buildGraph(filtered), 0);
    }
  });

  ngAfterViewInit() {
    this.loadData();
  }

  ngOnDestroy() {
    this.simulation?.stop();
    this.resizeObserver?.disconnect();
  }

  private loadData() {
    this.api.getClusterGraph().subscribe({
      next: data => {
        this.graphData.set(data);
        // Initialize all edge types as enabled
        const types = new Set<string>();
        for (const e of data.edges) {
          for (const t of Object.keys(e.typeCounts)) {
            types.add(t);
          }
        }
        this.enabledEdgeTypes.set(types);
        this.loading.set(false);
        // The filterEffect will handle building the graph
      },
      error: () => this.loading.set(false)
    });
  }

  setViewMode(mode: 'graph' | 'list') {
    this.viewMode.set(mode);
    if (mode === 'graph' && this.filteredGraphData()) {
      setTimeout(() => this.buildGraph(this.filteredGraphData()!), 0);
    }
  }

  selectCluster(id: number | null) {
    this.selectedClusterId.set(id);
    this.clusterDetail.set(null);
    this.highlightCluster(id);
    if (id !== null) {
      this.loadingDetail.set(true);
      this.api.getClusterDetail(id).subscribe({
        next: detail => {
          this.clusterDetail.set(detail);
          this.loadingDetail.set(false);
        },
        error: () => this.loadingDetail.set(false)
      });
    }
  }

  toggleEdgeType(type: string) {
    const current = this.enabledEdgeTypes();
    const next = new Set(current);
    if (next.has(type)) {
      next.delete(type);
    } else {
      next.add(type);
    }
    this.enabledEdgeTypes.set(next);
  }

  enableAllEdgeTypes() {
    this.enabledEdgeTypes.set(new Set(this.availableEdgeTypes()));
  }

  disableAllEdgeTypes() {
    this.enabledEdgeTypes.set(new Set());
  }

  isEdgeTypeEnabled(type: string): boolean {
    return this.enabledEdgeTypes().has(type);
  }

  formatEdgeType(type: string): string {
    return type.replace(/_/g, ' ').toLowerCase().replace(/\b\w/g, c => c.toUpperCase());
  }

  navigateToRepo(name: string) {
    this.router.navigate(['/repos', name]);
  }

  getClusterColor(clusterId: number): string {
    return this.colorScale(String(clusterId));
  }

  getClusterLabel(cluster: ClusterInfo): string {
    if (cluster.label) return cluster.label;
    const name = this.clusterNameMap().get(cluster.clusterId);
    if (name) return name;
    return `Cluster ${cluster.clusterId}`;
  }

  getClusterLabelById(clusterId: number): string {
    const clusters = this.clusters();
    const cluster = clusters.find(c => c.clusterId === clusterId);
    return cluster ? this.getClusterLabel(cluster) : `Cluster ${clusterId}`;
  }

  private buildGraph(data: ClusterGraphResponse) {
    if (!this.svgRef || !this.wrapperRef) return;

    const svgEl = this.svgRef.nativeElement;
    const wrapper = this.wrapperRef.nativeElement;
    const svg = d3.select(svgEl);
    svg.selectAll('*').remove();

    const width = wrapper.clientWidth;
    const height = wrapper.clientHeight || 600;
    svg.attr('viewBox', `0 0 ${width} ${height}`);

    // Resize handling
    this.resizeObserver?.disconnect();
    this.resizeObserver = new ResizeObserver(entries => {
      const { width: w, height: h } = entries[0].contentRect;
      if (w > 0 && h > 0) {
        svg.attr('viewBox', `0 0 ${w} ${h}`);
        this.simulation?.force('center', d3.forceCenter(w / 2, h / 2));
        this.simulation?.alpha(0.1).restart();
      }
    });
    this.resizeObserver.observe(wrapper);

    // Edge counts for node sizing
    const edgeCounts = new Map<string, number>();
    for (const e of data.edges) {
      edgeCounts.set(e.source, (edgeCounts.get(e.source) ?? 0) + e.count);
      edgeCounts.set(e.target, (edgeCounts.get(e.target) ?? 0) + e.count);
    }

    const radiusScale = d3.scaleSqrt()
      .domain([0, d3.max([...edgeCounts.values()]) ?? 1])
      .range([5, 22]);

    const nodes: SimNode[] = data.nodes
      .map(n => {
        const ec = edgeCounts.get(n.name) ?? 0;
        return {
          id: n.name,
          clusterId: n.clusterId ?? null,
          isFoundational: n.isFoundational,
          edgeCount: ec,
          betweenness: n.betweennessCentrality,
          radius: radiusScale(ec)
        };
      });

    const nodeMap = new Map(nodes.map(n => [n.id, n]));
    const isolatedNodes = nodes.filter(n => n.edgeCount === 0);

    const links: SimLink[] = data.edges
      .filter(e => nodeMap.has(e.source) && nodeMap.has(e.target))
      .map(e => ({
        source: e.source,
        target: e.target,
        count: e.count,
        isCrossCluster: e.isCrossCluster
      }));

    if (nodes.length === 0) {
      svg.append('text')
        .attr('x', width / 2).attr('y', height / 2)
        .attr('text-anchor', 'middle')
        .attr('fill', '#9ca3af')
        .text('No cluster data available. Index repositories first.');
      return;
    }

    const linkWidthScale = d3.scaleLinear()
      .domain([1, d3.max(links, l => l.count) ?? 1])
      .range([0.5, 4]);

    // Compute cluster centroids for cluster gravity forces
    const clusterNodes = new Map<number, SimNode[]>();
    for (const n of nodes) {
      if (n.clusterId !== null) {
        if (!clusterNodes.has(n.clusterId)) clusterNodes.set(n.clusterId, []);
        clusterNodes.get(n.clusterId)!.push(n);
      }
    }

    // Assign initial positions grouped by cluster (radial layout)
    const clusterIds = [...clusterNodes.keys()].sort();
    const angleStep = (2 * Math.PI) / Math.max(clusterIds.length, 1);
    const clusterRadius = Math.min(width, height) * 0.3;
    const clusterCentroids = new Map<number, { x: number; y: number }>();

    clusterIds.forEach((cid, i) => {
      const cx = width / 2 + clusterRadius * Math.cos(i * angleStep);
      const cy = height / 2 + clusterRadius * Math.sin(i * angleStep);
      clusterCentroids.set(cid, { x: cx, y: cy });

      const members = clusterNodes.get(cid)!;
      members.forEach((n, j) => {
        const a = (2 * Math.PI * j) / members.length;
        const r = 30 + members.length * 5;
        n.x = cx + r * Math.cos(a);
        n.y = cy + r * Math.sin(a);
      });
    });

    // Foundational nodes with edges get pushed to the periphery of connected clusters.
    const foundational = nodes.filter(n => n.isFoundational && n.edgeCount > 0);
    foundational.forEach((n, i) => {
      const a = (2 * Math.PI * i) / Math.max(foundational.length, 1);
      n.x = width / 2 + (clusterRadius + 80) * Math.cos(a);
      n.y = height / 2 + (clusterRadius + 80) * Math.sin(a);
    });

    const isolatedOrbitRadius = Math.min(width, height) * 0.44;
    const isolatedTargets = new Map<string, { x: number; y: number }>();
    isolatedNodes.forEach((n, i) => {
      const a = (2 * Math.PI * i) / Math.max(isolatedNodes.length, 1);
      const target = {
        x: width / 2 + isolatedOrbitRadius * Math.cos(a),
        y: height / 2 + isolatedOrbitRadius * Math.sin(a)
      };
      n.x = target.x;
      n.y = target.y;
      isolatedTargets.set(n.id, target);
    });

    // Zoom
    const g = svg.append('g');
    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.2, 5])
      .on('zoom', (event) => g.attr('transform', event.transform));
    svg.call(zoom);

    // Click on background to deselect
    svg.on('click', (event) => {
      if (event.target === svgEl) {
        this.selectCluster(null);
      }
    });

    // Links
    const linkG = g.append('g')
      .selectAll<SVGLineElement, SimLink>('line')
      .data(links)
      .join('line')
      .attr('stroke', d => d.isCrossCluster ? '#e5e7eb' : '#9ca3af')
      .attr('stroke-opacity', d => d.isCrossCluster ? 0.3 : 0.6)
      .attr('stroke-width', d => linkWidthScale(d.count))
      .attr('stroke-dasharray', d => d.isCrossCluster ? '4,3' : 'none');

    // Node groups
    const nodeG = g.append('g')
      .selectAll<SVGGElement, SimNode>('g')
      .data(nodes)
      .join('g')
      .attr('cursor', 'pointer')
      .on('click', (_event, d) => {
        if (d.clusterId !== null) {
          this.selectCluster(d.clusterId);
        }
        this.navigateToRepo(d.id);
      })
      .call(d3.drag<SVGGElement, SimNode>()
        .on('start', (event, d) => {
          if (!event.active) this.simulation!.alphaTarget(0.3).restart();
          d.fx = d.x; d.fy = d.y;
        })
        .on('drag', (event, d) => { d.fx = event.x; d.fy = event.y; })
        .on('end', (event, d) => {
          if (!event.active) this.simulation!.alphaTarget(0);
          d.fx = null; d.fy = null;
        }));

    // Bridge ring (betweenness centrality > 0.01)
    nodeG.filter(d => d.betweenness > 0.01)
      .append('circle')
      .attr('r', d => d.radius + 3)
      .attr('fill', 'none')
      .attr('stroke', '#ef4444')
      .attr('stroke-width', d => Math.min(3, 1 + d.betweenness * 20))
      .attr('stroke-opacity', 0.7);

    // Main circle
    nodeG.append('circle')
      .attr('r', d => d.radius)
      .attr('fill', d => {
        if (d.edgeCount === 0) return '#e5e7eb';
        if (d.isFoundational) return '#9ca3af';
        if (d.clusterId === null) return '#d1d5db';
        return this.colorScale(String(d.clusterId));
      })
      .attr('stroke', d => d.edgeCount === 0 ? '#94a3b8' : '#fff')
      .attr('stroke-width', 1.5);

    // Labels
    nodeG.append('text')
      .text(d => d.id)
      .attr('dy', d => d.radius + 12)
      .attr('text-anchor', 'middle')
      .attr('font-size', '10px')
      .attr('fill', '#374151')
      .attr('pointer-events', 'none');

    // Tooltip
    nodeG.append('title')
      .text(d => {
        const clusterLabel = d.clusterId !== null
          ? this.getClusterLabelById(d.clusterId)
          : (d.edgeCount === 0 ? 'Isolated' : 'Unclustered');
        const bridge = d.betweenness > 0.01 ? '\n(bridge repo)' : '';
        const edgeSummary = d.edgeCount === 0
          ? 'No cross-repo edges'
          : `${d.edgeCount} cross-repo edges`;
        return `${d.id}\n${clusterLabel}\n${edgeSummary}${bridge}`;
      });

    // Store selections for highlight updates
    this.nodeSelection = nodeG;
    this.linkSelection = linkG;

    // Apply highlight if a cluster is already selected
    this.highlightCluster(this.selectedClusterId());

    // Simulation with cluster gravity
    this.simulation = d3.forceSimulation<SimNode>(nodes)
      .force('link', d3.forceLink<SimNode, SimLink>(links)
        .id(d => d.id)
        .distance(d => d.isCrossCluster ? 150 : 60))
      .force('charge', d3.forceManyBody().strength(-120))
      .force('center', d3.forceCenter(width / 2, height / 2).strength(0.05))
      .force('collision', d3.forceCollide<SimNode>().radius(d => d.radius + 6))
      .force('clusterX', d3.forceX<SimNode>(d => {
        if (d.clusterId !== null && clusterCentroids.has(d.clusterId))
          return clusterCentroids.get(d.clusterId)!.x;
        return width / 2;
      }).strength(d => d.edgeCount === 0 ? 0 : (d.clusterId !== null ? 0.15 : 0.02)))
      .force('clusterY', d3.forceY<SimNode>(d => {
        if (d.clusterId !== null && clusterCentroids.has(d.clusterId))
          return clusterCentroids.get(d.clusterId)!.y;
        return height / 2;
      }).strength(d => d.edgeCount === 0 ? 0 : (d.clusterId !== null ? 0.15 : 0.02)))
      .force('orbitX', d3.forceX<SimNode>(d => isolatedTargets.get(d.id)?.x ?? width / 2)
        .strength(d => d.edgeCount === 0 ? 0.28 : 0))
      .force('orbitY', d3.forceY<SimNode>(d => isolatedTargets.get(d.id)?.y ?? height / 2)
        .strength(d => d.edgeCount === 0 ? 0.28 : 0))
      .on('tick', () => {
        linkG
          .attr('x1', d => (d.source as SimNode).x!)
          .attr('y1', d => (d.source as SimNode).y!)
          .attr('x2', d => (d.target as SimNode).x!)
          .attr('y2', d => (d.target as SimNode).y!);
        nodeG.attr('transform', d => `translate(${d.x},${d.y})`);
      });
  }

  private highlightCluster(clusterId: number | null) {
    if (!this.nodeSelection || !this.linkSelection) return;

    if (clusterId === null) {
      // Reset everything to default
      this.nodeSelection.attr('opacity', 1);
      this.linkSelection
        .attr('stroke-opacity', d => d.isCrossCluster ? 0.3 : 0.6);
      // Reset node order
      this.nodeSelection.order();
      return;
    }

    // Dim non-selected, bring selected to front
    this.nodeSelection
      .attr('opacity', d => d.clusterId === clusterId ? 1 : 0.15)
      .sort((a, b) => {
        // Selected cluster nodes rendered last (on top)
        const aIn = a.clusterId === clusterId ? 1 : 0;
        const bIn = b.clusterId === clusterId ? 1 : 0;
        return aIn - bIn;
      });

    this.linkSelection
      .attr('stroke-opacity', d => {
        const src = d.source as SimNode;
        const tgt = d.target as SimNode;
        if (src.clusterId === clusterId || tgt.clusterId === clusterId) {
          return d.isCrossCluster ? 0.5 : 0.8;
        }
        return 0.03;
      });
  }
}
