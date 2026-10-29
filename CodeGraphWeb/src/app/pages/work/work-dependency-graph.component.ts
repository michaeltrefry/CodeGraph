import { AfterViewInit, Component, ElementRef, OnDestroy, computed, effect, input, output, viewChild } from '@angular/core';
import * as d3 from 'd3';
import { NODE_HEIGHT, NODE_WIDTH, WorkGraph, buildWorkGraph } from './work-graph';
import { blockedLabel, statusLabel } from './work-shared';
import { ItemView } from './work.models';

/**
 * The dependency graph of a feature: what blocks what, the critical path, what can run in parallel.
 * Blocked (waiting, awaiting a person) and cancelled-prerequisite items are told apart by colour and
 * by a label, never by colour alone. d3 draws the SVG and handles zoom and pan.
 */
@Component({
  selector: 'wrk-dependency-graph',
  standalone: true,
  template: `
    <div class="wrk-legend" aria-hidden="true">
      <span><i data-k="ready"></i>ready now</span>
      <span><i data-k="blocked"></i>blocked (waiting)</span>
      <span><i data-k="human"></i>awaiting a person</span>
      <span><i data-k="cancelled"></i>cancelled prerequisite</span>
      <span><i data-k="critical"></i>critical path</span>
    </div>
    <div class="wrk-graph-wrap">
      <svg #svg role="img" [attr.aria-label]="summary()"></svg>
    </div>
    <div class="wrk-grid" style="margin-top:12px">
      <section class="wrk-card" aria-labelledby="wrk-h-crit">
        <h2 id="wrk-h-crit">Critical path <span class="wrk-count">{{ graph().criticalPath.length }}</span></h2>
        @if (graph().criticalPath.length === 0) { <div class="wrk-empty">Nothing unfinished.</div> }
        <ol class="wrk-crit">@for (key of graph().criticalPath; track key) { <li><button type="button" class="wrk-btn wrk-btn-quiet" (click)="selected.emit(key)">{{ key }}</button> {{ titleOf(key) }}</li> }</ol>
      </section>
      <section class="wrk-card" aria-labelledby="wrk-h-par">
        <h2 id="wrk-h-par">Can run in parallel</h2>
        @if (graph().parallelSets.length === 0) { <div class="wrk-empty">Nothing to run.</div> }
        @for (set of graph().parallelSets; track set.wave) {
          <div class="wrk-row" data-testid="parallel-set">
            <strong>{{ set.wave === 0 ? 'Now' : 'Then, wave ' + set.wave }}</strong>
            @for (key of set.keys; track key) { <button type="button" class="wrk-btn wrk-btn-quiet" (click)="selected.emit(key)">{{ key }}</button> }
          </div>
        }
      </section>
      <section class="wrk-card" aria-labelledby="wrk-h-blk">
        <h2 id="wrk-h-blk">Blocked <span class="wrk-count">{{ blocked().length }}</span></h2>
        @if (blocked().length === 0) { <div class="wrk-empty">Nothing is blocked.</div> }
        <ul class="wrk-list">
          @for (node of blocked(); track node.key) {
            <li data-testid="blocked-item"><div class="wrk-row"><button type="button" class="wrk-btn wrk-btn-quiet" (click)="selected.emit(node.key)">{{ node.key }}</button>
              <span class="wrk-chip wrk-chip-blocked" [attr.data-reason]="node.blockedReason">{{ label(node.blockedReason) }}</span>
              @if (node.stuck && !node.cancelledPrerequisite) { <span class="wrk-chip wrk-chip-blocked" data-reason="cancelled_prerequisite">stuck behind a closed item</span> }
              <span class="wrk-clip">{{ node.title }}</span></div></li>
          }
        </ul>
      </section>
    </div>
  `,
  styles: [`.wrk-crit { margin: 0; padding-left: 20px; }`]
})
export class DependencyGraphComponent implements AfterViewInit, OnDestroy {
  readonly items = input.required<ItemView[]>();
  readonly selected = output<string>();
  private readonly svg = viewChild.required<ElementRef<SVGSVGElement>>('svg');
  readonly graph = computed<WorkGraph>(() => buildWorkGraph(this.items()));
  readonly blocked = computed(() => this.graph().nodes.filter(node => !node.finished && (node.blocked || node.stuck)));
  readonly summary = computed(() => {
    const graph = this.graph();
    return `Dependency graph of ${graph.nodes.length} items: ${graph.criticalPath.length} on the critical path, ${this.blocked().length} blocked.`;
  });
  private ready = false;
  private zoomed?: { x: number; y: number; k: number };

  constructor() {
    effect(() => {
      const graph = this.graph();
      if (this.ready) this.draw(graph);
    });
  }

  ngAfterViewInit(): void {
    this.ready = true;
    this.draw(this.graph());
  }

  ngOnDestroy(): void {
    this.ready = false;
    d3.select(this.svg().nativeElement).on('.zoom', null);
  }

  titleOf(key: string): string {
    return this.graph().nodes.find(node => node.key === key)?.title ?? '';
  }

  label = blockedLabel;

  private draw(graph: WorkGraph): void {
    const svg = d3.select(this.svg().nativeElement);
    svg.selectAll('*').remove();
    svg.attr('viewBox', `0 0 ${Math.max(graph.width, 200)} ${Math.max(graph.height, 100)}`);
    const defs = svg.append('defs');
    defs.append('marker').attr('id', 'wrk-arrow').attr('viewBox', '0 0 10 10').attr('refX', 9).attr('refY', 5).attr('markerWidth', 6).attr('markerHeight', 6).attr('orient', 'auto-start-reverse')
      .append('path').attr('d', 'M0,0 L10,5 L0,10 z').attr('fill', 'currentColor').style('color', 'var(--muted)');
    const layer = svg.append('g').attr('class', 'wrk-g-layer');
    const byKey = new Map(graph.nodes.map(node => [node.key, node]));

    layer.append('g').selectAll('path')
      .data(graph.edges)
      .join('path')
      .attr('class', edge => `wrk-g-edge${edge.critical ? ' critical' : ''}${edge.cancelled ? ' cancelled' : ''}`)
      .attr('data-from', edge => edge.from)
      .attr('data-to', edge => edge.to)
      .attr('marker-end', 'url(#wrk-arrow)')
      .attr('d', edge => {
        const from = byKey.get(edge.from)!;
        const to = byKey.get(edge.to)!;
        const x1 = from.x + NODE_WIDTH;
        const y1 = from.y + NODE_HEIGHT / 2;
        const x2 = to.x;
        const y2 = to.y + NODE_HEIGHT / 2;
        const mid = (x1 + x2) / 2;
        return `M${x1},${y1} C${mid},${y1} ${mid},${y2} ${x2},${y2}`;
      });

    const node = layer.append('g').selectAll('g')
      .data(graph.nodes)
      .join('g')
      .attr('class', n => `wrk-g-node${n.onCriticalPath ? ' critical' : ''}`)
      .attr('data-key', n => n.key)
      .attr('data-status', n => n.status)
      .attr('data-blocked', n => (n.blocked ? 'true' : null))
      .attr('data-reason', n => n.blockedReason ?? null)
      .attr('data-stuck', n => (n.stuck ? 'true' : null))
      .attr('data-ready', n => (n.ready ? 'true' : null))
      .attr('transform', n => `translate(${n.x},${n.y})`)
      .attr('tabindex', 0)
      .attr('role', 'button')
      .attr('aria-label', n => `${n.key} ${n.title}, ${statusLabel(n.status)}${n.blocked ? ', ' + blockedLabel(n.blockedReason) : ''}${n.stuck ? ', stuck behind a closed item' : ''}${n.onCriticalPath ? ', on the critical path' : ''}`)
      .on('click', (_event, n) => this.selected.emit(n.key))
      .on('keydown', (event: KeyboardEvent, n) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          this.selected.emit(n.key);
        }
      });
    node.append('title').text(n => `${n.key}: ${n.title}`);
    node.append('rect').attr('width', NODE_WIDTH).attr('height', NODE_HEIGHT).attr('rx', 6);
    node.append('text').attr('class', 'key').attr('x', 8).attr('y', 15).text(n => `${n.key} · ${n.blockedReason ? blockedLabel(n.blockedReason) : statusLabel(n.status)}${n.stuck && !n.cancelledPrerequisite ? ' · stuck' : ''}`);
    node.append('text').attr('x', 8).attr('y', 31).text(n => (n.title.length > 27 ? `${n.title.slice(0, 26)}…` : n.title));

    try {
      const behavior = d3.zoom<SVGSVGElement, unknown>()
        .scaleExtent([0.3, 3])
        .on('zoom', event => {
          this.zoomed = { x: event.transform.x, y: event.transform.y, k: event.transform.k };
          layer.attr('transform', event.transform.toString());
        });
      svg.call(behavior);
      if (this.zoomed) svg.call(behavior.transform, d3.zoomIdentity.translate(this.zoomed.x, this.zoomed.y).scale(this.zoomed.k));
    } catch {
      // Zoom needs layout APIs; the graph is still drawn without it.
    }
  }
}
