import { AfterViewInit, Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, ViewChild } from '@angular/core';
import * as d3 from 'd3';
import { MemoryGraphLink, MemoryGraphNode } from '../../core/models';

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
  selector: 'app-memory-graph-view',
  standalone: true,
  template: `
    <div class="graph-wrapper" #wrapper>
      <svg #svg role="img" [attr.aria-label]="ariaLabel"></svg>
      <div class="graph-hint">{{ hint }}</div>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex: 1 1 auto;
      min-width: 0;
      min-height: 0;
    }

    .graph-wrapper {
      position: relative;
      flex: 1 1 auto;
      min-width: 0;
      min-height: 0;
    }

    svg {
      display: block;
      width: 100%;
      height: 100%;
    }

    .graph-hint {
      position: absolute;
      left: 12px;
      bottom: 12px;
      max-width: min(560px, calc(100% - 24px));
      padding: 6px 9px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: color-mix(in oklab, var(--surface) 84%, transparent);
      color: var(--muted);
      font-size: var(--fs-xs);
      pointer-events: none;
      backdrop-filter: blur(6px);
      -webkit-backdrop-filter: blur(6px);
    }
  `]
})
export class MemoryGraphViewComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input() nodes: MemoryGraphNode[] = [];
  @Input() links: MemoryGraphLink[] = [];
  @Input() focusedEntityId: string | null = null;
  @Input() hint = 'Showing the most recently updated memory entities in a bounded graph';
  @Input() ariaLabel = 'Memory graph visualization';

  @Output() selectEntity = new EventEmitter<string>();

  @ViewChild('svg', { static: true }) private svgRef!: ElementRef<SVGSVGElement>;
  @ViewChild('wrapper', { static: true }) private wrapperRef!: ElementRef<HTMLDivElement>;

  private simulation?: d3.Simulation<SimNode, SimLink>;
  private resizeObserver?: ResizeObserver;
  private colorScale = d3.scaleOrdinal<string, string>();
  private viewInitialized = false;

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    this.renderGraph();
  }

  ngOnChanges(_changes: SimpleChanges): void {
    if (this.viewInitialized) {
      this.renderGraph();
    }
  }

  ngOnDestroy(): void {
    this.simulation?.stop();
    this.resizeObserver?.disconnect();
  }

  private renderGraph(): void {
    const svgElement = this.svgRef.nativeElement;
    const wrapper = this.wrapperRef.nativeElement;
    const svg = d3.select(svgElement);
    svg.selectAll('*').remove();

    const width = wrapper.clientWidth;
    const height = wrapper.clientHeight || 620;
    const colors = this.getGraphColors(wrapper);
    this.colorScale.range(colors.nodePalette);
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
    for (const link of this.links) {
      degrees.set(link.source, (degrees.get(link.source) ?? 0) + 1);
      degrees.set(link.target, (degrees.get(link.target) ?? 0) + 1);
    }

    const radiusScale = d3.scaleSqrt()
      .domain([0, d3.max([...degrees.values()]) ?? 1])
      .range([5, this.focusedEntityId ? 16 : 10]);

    const nodes: SimNode[] = this.nodes.map(node => ({
      id: node.id,
      label: node.label,
      type: node.type,
      summary: node.summary,
      degree: degrees.get(node.id) ?? 0,
      radius: node.id === this.focusedEntityId ? 20 : radiusScale(degrees.get(node.id) ?? 0)
    }));

    const nodeMap = new Map(nodes.map(node => [node.id, node]));
    const updatedAtById = new Map(this.nodes.map(node => [node.id, node.updatedAt]));
    const links: SimLink[] = this.links
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
        .attr('fill', colors.muted)
        .text('No memory nodes to display.');
      return;
    }

    const showLabels = !!this.focusedEntityId || nodes.length <= 80;
    const labeledNodeIds = new Set<string>();
    if (!showLabels) {
      const topByDegree = [...nodes]
        .sort((a, b) => b.degree - a.degree || a.label.localeCompare(b.label))
        .slice(0, 16);

      const topByRecency = [...nodes]
        .sort((a, b) => this.compareByRecencyAndLabel(updatedAtById.get(a.id), updatedAtById.get(b.id), a.label, b.label))
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
      .selectAll('line')
      .data(links)
      .join('line')
      .style('stroke', colors.link)
      .attr('stroke-opacity', this.focusedEntityId ? 0.55 : 0.72)
      .attr('stroke-width', this.focusedEntityId ? 1.6 : 1.1);

    const nodeSelection = g.append('g')
      .selectAll<SVGGElement, SimNode>('g')
      .data(nodes)
      .join('g')
      .attr('cursor', 'pointer');

    const isLabelVisible = (node: SimNode) => showLabels || labeledNodeIds.has(node.id);

    const labelSelection = nodeSelection.append('text')
      .text(node => node.label)
      .attr('display', node => isLabelVisible(node) ? null : 'none')
      .attr('dy', node => node.radius + 14)
      .attr('text-anchor', 'middle')
      .attr('font-size', '11px')
      .style('font-family', 'var(--font-mono)')
      .style('fill', colors.label)
      .attr('pointer-events', 'none');

    nodeSelection
      .on('mouseenter', (_event, node) => {
        if (isLabelVisible(node)) return;
        labelSelection.filter(candidate => candidate.id === node.id).attr('display', null);
      })
      .on('mouseleave', (_event, node) => {
        if (isLabelVisible(node)) return;
        labelSelection.filter(candidate => candidate.id === node.id).attr('display', 'none');
      })
      .on('click', (_event, node) => this.selectEntity.emit(node.id))
      .call(d3.drag<SVGGElement, SimNode>()
        .on('start', (event, node) => {
          if (!event.active) this.simulation?.alphaTarget(0.3).restart();
          node.fx = node.x;
          node.fy = node.y;
        })
        .on('drag', (event, node) => {
          node.fx = event.x;
          node.fy = event.y;
        })
        .on('end', (event, node) => {
          if (!event.active) this.simulation?.alphaTarget(0);
          node.fx = null;
          node.fy = null;
        }));

    nodeSelection.append('circle')
      .attr('r', node => node.radius)
      .style('fill', node => this.colorScale(node.type))
      .attr('fill-opacity', node => node.id === this.focusedEntityId ? 0.32 : 0.18)
      .style('stroke', node => node.id === this.focusedEntityId ? colors.focusStroke : this.colorScale(node.type))
      .attr('stroke-width', node => node.id === this.focusedEntityId ? 2.5 : 1.5)
      .attr('opacity', node => this.focusedEntityId && node.id !== this.focusedEntityId ? 0.9 : 1);

    nodeSelection.append('title')
      .text(node => `${node.label}\n${this.formatType(node.type)}\n${node.degree} visible links${node.summary ? `\n\n${node.summary}` : ''}`);

    this.simulation?.stop();
    this.simulation = d3.forceSimulation<SimNode>(nodes)
      .force('link', d3.forceLink<SimNode, SimLink>(links)
        .id(node => node.id)
        .distance(this.focusedEntityId ? 110 : 55))
      .force('charge', d3.forceManyBody().strength(this.focusedEntityId ? -360 : -80))
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('collision', d3.forceCollide<SimNode>().radius(node => node.radius + (this.focusedEntityId ? 18 : 6)))
      .force('focusX', d3.forceX<SimNode>(width / 2)
        .strength(node => node.id === this.focusedEntityId ? 0.45 : 0.03))
      .force('focusY', d3.forceY<SimNode>(height / 2)
        .strength(node => node.id === this.focusedEntityId ? 0.45 : 0.03))
      .on('tick', () => {
        linkSelection
          .attr('x1', link => (link.source as SimNode).x ?? 0)
          .attr('y1', link => (link.source as SimNode).y ?? 0)
          .attr('x2', link => (link.target as SimNode).x ?? 0)
          .attr('y2', link => (link.target as SimNode).y ?? 0);

        nodeSelection.attr('transform', node => `translate(${node.x ?? 0},${node.y ?? 0})`);
      });

    const focusedNode = this.focusedEntityId ? nodeMap.get(this.focusedEntityId) : null;
    if (focusedNode) {
      focusedNode.fx = width / 2;
      focusedNode.fy = height / 2;
      this.simulation.alpha(0.7).restart();
    }
  }

  private compareByRecencyAndLabel(aDate: string | undefined, bDate: string | undefined, aLabel: string, bLabel: string): number {
    const aTime = aDate ? Date.parse(aDate) : 0;
    const bTime = bDate ? Date.parse(bDate) : 0;
    if (bTime !== aTime) {
      return bTime - aTime;
    }
    return aLabel.localeCompare(bLabel);
  }

  private getGraphColors(element: HTMLElement): {
    muted: string;
    label: string;
    link: string;
    focusStroke: string;
    nodePalette: string[];
  } {
    const style = getComputedStyle(element);
    const token = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;

    return {
      muted: token('--muted', 'GrayText'),
      label: token('--text-2', 'CanvasText'),
      link: token('--border-2', 'GrayText'),
      focusStroke: token('--text', 'CanvasText'),
      nodePalette: [
        token('--accent', 'Highlight'),
        token('--sem-cyan', 'DeepSkyBlue'),
        token('--sem-green', 'SeaGreen'),
        token('--sem-amber', 'GoldenRod'),
        token('--sem-purple', 'MediumPurple'),
        token('--sem-blue', 'DodgerBlue'),
        token('--sem-red', 'IndianRed'),
        token('--text-2', 'CanvasText')
      ]
    };
  }

  private formatType(value: string): string {
    return value
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/\b\w/g, char => char.toUpperCase());
  }
}
