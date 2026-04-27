import {
  Component, ElementRef, ViewChild, AfterViewInit,
  OnDestroy, inject, signal, input, output
} from '@angular/core';
import * as d3 from 'd3';
import { ApiService } from '../../core/api.service';
import { GraphOverviewNode, GraphOverviewEdge } from '../../core/models';

interface SimNode extends d3.SimulationNodeDatum {
  id: string;
  language?: string;
  framework?: string;
  isFoundational: boolean;
  edgeCount: number;    // total edges touching this node
  radius: number;
}

interface SimLink extends d3.SimulationLinkDatum<SimNode> {
  count: number;
  types: Record<string, number>;
}

@Component({
  selector: 'app-repo-graph',
  standalone: true,
  template: `
    <div class="graph-wrapper" #wrapper role="group" aria-label="Repository dependency graph">
      @if (loading()) {
        <div class="graph-loading">Loading graph…</div>
      }
      <svg #svg role="img" aria-label="Cross-repository dependency graph"></svg>
      <div class="graph-legend">
        <span class="legend-item"><span class="legend-dot foundational"></span> Foundational</span>
        <span class="legend-item"><span class="legend-dot regular"></span> Application</span>
        <span class="legend-item"><span class="legend-dot isolated"></span> Isolated</span>
        <span class="legend-hint">Scroll to zoom · Drag to pan · Click a node to navigate</span>
      </div>
    </div>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; flex: 1; min-height: 0; }
    .graph-wrapper {
      position: relative;
      background:
        radial-gradient(ellipse at center, color-mix(in oklab, var(--accent) 6%, transparent) 0%, transparent 60%),
        radial-gradient(ellipse at bottom left, color-mix(in oklab, var(--sem-purple) 4%, transparent) 0%, transparent 50%),
        var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      overflow: hidden;
      flex: 1;
      min-height: 300px;
    }
    svg { display: block; width: 100%; height: 100%; }
    .graph-loading {
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      color: var(--muted);
      font-size: var(--fs-md);
      z-index: 1;
    }
    .graph-legend {
      position: absolute;
      bottom: 10px;
      left: 14px;
      display: flex;
      align-items: center;
      gap: 14px;
      font-size: var(--fs-xs);
      color: var(--muted);
    }
    .legend-item {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .legend-dot {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      display: inline-block;
    }
    .legend-dot.foundational { background: var(--sem-amber); }
    .legend-dot.regular { background: var(--accent); }
    .legend-dot.isolated {
      background: var(--surface-3);
      border: 1px dashed var(--faint);
      box-sizing: border-box;
    }
    .legend-hint { margin-left: auto; font-style: italic; }
  `]
})
export class RepoGraphComponent implements AfterViewInit, OnDestroy {
  @ViewChild('svg', { static: true }) svgRef!: ElementRef<SVGSVGElement>;
  @ViewChild('wrapper', { static: true }) wrapperRef!: ElementRef<HTMLDivElement>;

  nodeClicked = output<string>();

  private api = inject(ApiService);
  loading = signal(true);
  private simulation?: d3.Simulation<SimNode, SimLink>;
  private resizeObserver?: ResizeObserver;

  ngAfterViewInit() {
    this.api.getGraphOverview().subscribe({
      next: data => {
        this.loading.set(false);
        this.buildGraph(data.nodes, data.edges);
      },
      error: () => this.loading.set(false)
    });
  }

  ngOnDestroy() {
    this.simulation?.stop();
    this.resizeObserver?.disconnect();
  }

  private buildGraph(rawNodes: GraphOverviewNode[], rawEdges: GraphOverviewEdge[]) {
    const svg = d3.select(this.svgRef.nativeElement);
    const wrapper = this.wrapperRef.nativeElement;
    const width = wrapper.clientWidth;
    const height = wrapper.clientHeight || 500;

    svg.attr('viewBox', `0 0 ${width} ${height}`);

    // Update viewBox and simulation center on resize
    this.resizeObserver = new ResizeObserver(entries => {
      const { width: w, height: h } = entries[0].contentRect;
      if (w > 0 && h > 0) {
        svg.attr('viewBox', `0 0 ${w} ${h}`);
        this.simulation?.force('center', d3.forceCenter(w / 2, h / 2));
        this.simulation?.alpha(0.1).restart();
      }
    });
    this.resizeObserver.observe(wrapper);

    // Build edge count per node for sizing
    const edgeCounts = new Map<string, number>();
    for (const e of rawEdges) {
      edgeCounts.set(e.source, (edgeCounts.get(e.source) ?? 0) + e.count);
      edgeCounts.set(e.target, (edgeCounts.get(e.target) ?? 0) + e.count);
    }

    const radiusScale = d3.scaleSqrt()
      .domain([0, d3.max([...edgeCounts.values()]) ?? 1])
      .range([4, 20]);

    const nodes: SimNode[] = rawNodes
      .map(n => {
        const ec = edgeCounts.get(n.name) ?? 0;
        return {
          id: n.name,
          language: n.language ?? undefined,
          framework: n.framework ?? undefined,
          isFoundational: n.isFoundational,
          edgeCount: ec,
          radius: radiusScale(ec)
        };
      });

    const nodeMap = new Map(nodes.map(n => [n.id, n]));
    const isolatedNodes = nodes.filter(n => n.edgeCount === 0);

    const links: SimLink[] = rawEdges
      .filter(e => nodeMap.has(e.source) && nodeMap.has(e.target))
      .map(e => ({
        source: e.source,
        target: e.target,
        count: e.count,
        types: e.typeCounts
      }));

    if (nodes.length === 0) {
      svg.append('text')
        .attr('x', width / 2).attr('y', height / 2)
        .attr('text-anchor', 'middle')
        .style('fill', 'var(--muted)')
        .text('No repositories found.');
      return;
    }

    const linkWidthScale = d3.scaleLinear()
      .domain([1, d3.max(links, l => l.count) ?? 1])
      .range([0.5, 4]);

    const orbitRadius = Math.min(width, height) * 0.42;
    const orbitTargets = new Map<string, { x: number; y: number }>();

    isolatedNodes.forEach((node, index) => {
      const angle = (2 * Math.PI * index) / Math.max(isolatedNodes.length, 1);
      const target = {
        x: width / 2 + orbitRadius * Math.cos(angle),
        y: height / 2 + orbitRadius * Math.sin(angle)
      };

      node.x = target.x;
      node.y = target.y;
      orbitTargets.set(node.id, target);
    });

    // Zoom
    const g = svg.append('g');
    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.2, 5])
      .on('zoom', (event) => g.attr('transform', event.transform));
    svg.call(zoom);

    // Links
    const linkG = g.append('g')
      .style('stroke', 'var(--border-2)')
      .attr('stroke-opacity', 0.6)
      .selectAll('line')
      .data(links)
      .join('line')
      .attr('stroke-width', d => linkWidthScale(d.count));

    // Node groups
    const nodeG = g.append('g')
      .selectAll<SVGGElement, SimNode>('g')
      .data(nodes)
      .join('g')
      .attr('cursor', 'pointer')
      .on('click', (_event, d) => this.nodeClicked.emit(d.id))
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

    nodeG.append('circle')
      .attr('r', d => d.radius)
      .style('fill', d => d.edgeCount === 0 ? 'var(--surface-3)' : (d.isFoundational ? 'var(--sem-amber)' : 'var(--accent)'))
      .attr('fill-opacity', d => d.edgeCount === 0 ? 0.62 : 0.78)
      .style('stroke', d => d.edgeCount === 0 ? 'var(--faint)' : 'var(--surface)')
      .attr('stroke-width', 1.5);

    nodeG.append('text')
      .text(d => this.displayRepoName(d.id))
      .attr('dy', d => d.radius + 12)
      .attr('text-anchor', 'middle')
      .attr('font-size', '10px')
      .style('font-family', 'var(--font-mono)')
      .style('fill', 'var(--text-2)')
      .attr('pointer-events', 'none');

    // Tooltip on hover
    nodeG.append('title')
      .text(d => {
        const edgeSummary = d.edgeCount === 0
          ? 'No cross-repo edges'
          : `${d.edgeCount} cross-repo edges`;
        return `${d.id}\n${edgeSummary}${d.isFoundational ? '\n(foundational)' : ''}`;
      });

    // Simulation
    this.simulation = d3.forceSimulation<SimNode>(nodes)
      .force('link', d3.forceLink<SimNode, SimLink>(links)
        .id(d => d.id)
        .distance(80))
      .force('charge', d3.forceManyBody().strength(-150))
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('orbitX', d3.forceX<SimNode>(d => orbitTargets.get(d.id)?.x ?? width / 2)
        .strength(d => d.edgeCount === 0 ? 0.25 : 0.02))
      .force('orbitY', d3.forceY<SimNode>(d => orbitTargets.get(d.id)?.y ?? height / 2)
        .strength(d => d.edgeCount === 0 ? 0.25 : 0.02))
      .force('collision', d3.forceCollide<SimNode>().radius(d => d.radius + 8))
      .on('tick', () => {
        linkG
          .attr('x1', d => (d.source as SimNode).x!)
          .attr('y1', d => (d.source as SimNode).y!)
          .attr('x2', d => (d.target as SimNode).x!)
          .attr('y2', d => (d.target as SimNode).y!);
        nodeG.attr('transform', d => `translate(${d.x},${d.y})`);
      });
  }

  private displayRepoName(name: string): string {
    return name
      .replace(/^TC\./, '')
      .replace(/^CodeGraph\./, '')
      .replace(/Api$/, '');
  }
}
