import { Component, inject, OnInit, signal, computed, AfterViewChecked, ElementRef, viewChild } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import {
  NodeDetailResponse,
  NodeSourceResponse,
  EdgeSummary,
  CrossRepoEdgeSummary,
  LABEL_ICONS
} from '../../core/models';
import hljs from 'highlight.js';

interface SourceLine {
  num: number;
  html: SafeHtml;
  highlighted: boolean;
}

@Component({
  selector: 'app-node-detail',
  imports: [RouterLink],
  templateUrl: './node-detail.component.html',
  styleUrl: './node-detail.component.scss'
})
export class NodeDetailComponent implements OnInit, AfterViewChecked {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private sanitizer = inject(DomSanitizer);
  private chatContext = inject(ChatContextService);

  detail = signal<NodeDetailResponse | null>(null);
  source = signal<NodeSourceResponse | null>(null);
  sourceLoading = signal(false);
  loading = signal(true);
  trustToggling = signal(false);
  private needsScroll = false;

  readonly labelIcons = LABEL_ICONS;
  readonly sourceViewer = viewChild<ElementRef<HTMLElement>>('sourceViewer');

  sourceLines = computed<SourceLine[]>(() => {
    const src = this.source();
    if (!src) return [];
    const hl = this.highlightLine();
    const result = hljs.highlight(src.content, { language: src.language, ignoreIllegals: true });
    const htmlLines = result.value.split('\n');
    return htmlLines.map((html, i) => {
      const lineNum = i + 1;
      // If a specific line was requested via query param, highlight a 5-line window around it
      const highlighted = hl
        ? lineNum >= hl - 2 && lineNum <= hl + 2
        : lineNum >= src.startLine && lineNum <= src.endLine;
      return {
        num: lineNum,
        html: this.sanitizer.bypassSecurityTrustHtml(html || '&nbsp;'),
        highlighted
      };
    });
  });

  highlightLine = signal<number | null>(null);

  ngOnInit() {
    this.route.paramMap.subscribe(params => {
      const id = +(params.get('id') ?? '0');
      const line = this.route.snapshot.queryParamMap.get('line');
      this.highlightLine.set(line ? +line : null);
      this.load(id);
    });
  }

  ngAfterViewChecked() {
    if (this.needsScroll) {
      this.scrollToHighlighted();
      this.needsScroll = false;
    }
  }

  private load(id: number) {
    this.loading.set(true);
    this.detail.set(null);
    this.api.getNode(id).subscribe({
      next: d => {
        this.detail.set(d);
        this.loading.set(false);
        this.chatContext.setNode(d.node.id, d.node.name, d.node.label, d.node.project);
        if (d.node.filePath) this.loadSource(id);
      },
      error: () => this.loading.set(false)
    });
  }

  private loadSource(id: number) {
    this.sourceLoading.set(true);
    this.api.getNodeSource(id).subscribe({
      next: src => {
        this.source.set(src);
        this.sourceLoading.set(false);
        this.needsScroll = true;
      },
      error: () => this.sourceLoading.set(false)
    });
  }

  private scrollToHighlighted() {
    const container = this.sourceViewer()?.nativeElement;
    if (!container) return;
    const firstHighlighted = container.querySelector('.line-highlight');
    if (firstHighlighted) {
      firstHighlighted.scrollIntoView({ block: 'center' });
    }
  }

  icon(label?: string) {
    return label ? (this.labelIcons[label] ?? '•') : '•';
  }

  toggleDoNotTrust() {
    const d = this.detail();
    if (!d) return;
    const newValue = !d.node.doNotTrust;
    this.trustToggling.set(true);
    this.api.setDoNotTrust(d.node.id, newValue).subscribe({
      next: () => {
        this.detail.set({
          ...d,
          node: { ...d.node, doNotTrust: newValue }
        });
        this.trustToggling.set(false);
      },
      error: () => this.trustToggling.set(false)
    });
  }

  propertyEntries() {
    const d = this.detail();
    if (!d) return [];
    return Object.entries(d.node.properties ?? {});
  }

  outboundByType() {
    return this.groupByType(this.detail()?.outboundEdges ?? []);
  }

  inboundByType() {
    return this.groupByType(this.detail()?.inboundEdges ?? []);
  }

  private groupByType(edges: EdgeSummary[]) {
    const map = new Map<string, EdgeSummary[]>();
    for (const e of edges) {
      const list = map.get(e.type) ?? [];
      list.push(e);
      map.set(e.type, list);
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }

  crossRepoOutbound() {
    return this.detail()?.crossRepoEdges.filter(e => e.direction === 'outbound') ?? [];
  }

  crossRepoInbound() {
    return this.detail()?.crossRepoEdges.filter(e => e.direction === 'inbound') ?? [];
  }

  edgePropEntries(props: Record<string, unknown> | null | undefined): [string, unknown][] {
    return props ? Object.entries(props) : [];
  }
}
