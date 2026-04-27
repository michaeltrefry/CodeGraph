import { Component, computed, inject, signal, DestroyRef } from '@angular/core';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, map } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  ImpactReport, ImpactSummary, AffectedNode, RiskLevel,
  LABEL_ICONS
} from '../../core/models';
import { TypeaheadComponent, TypeaheadItem } from '../../shared/typeahead.component';

@Component({
  selector: 'app-impact',
  imports: [RouterLink, FormsModule, TypeaheadComponent],
  templateUrl: './impact.component.html',
  styleUrl: './impact.component.scss'
})
export class ImpactComponent {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);

  // Form inputs
  nodeName = signal('');
  projectName = signal('');
  depth = signal(3);

  // Results
  report = signal<ImpactReport | null>(null);
  loading = signal(false);
  error = signal<string | null>(null);
  searched = signal(false);

  // Selection state for the detail panel
  selectedNodeId = signal<number | null>(null);

  readonly riskLevels: RiskLevel[] = ['Critical', 'High', 'Medium', 'Low'];
  readonly labelIcons = LABEL_ICONS;

  /** Affected nodes sorted by (depth asc, then name asc). */
  treeRows = computed<AffectedNode[]>(() => {
    const nodes = this.report()?.affectedNodes ?? [];
    return [...nodes].sort((a, b) => (a.depth - b.depth) || a.name.localeCompare(b.name));
  });

  selectedNode = computed<AffectedNode | null>(() => {
    const id = this.selectedNodeId();
    if (id === null) return null;
    return this.report()?.affectedNodes.find(n => n.nodeId === id) ?? null;
  });

  /** Primary changed node - use first for the header strip "NODE" label. */
  primaryChanged = computed(() => this.report()?.changedNodes[0] ?? null);

  // Typeahead search functions
  searchProjects = (query: string): Observable<TypeaheadItem[]> =>
    this.api.listProjects(query, undefined, 1, 25).pipe(
      map(r => r.items.map(p => ({
        value: p.name,
        label: p.name,
        description: [p.sourceGroup, p.framework].filter(Boolean).join(' / ')
      })))
    );

  searchNodes = (query: string): Observable<TypeaheadItem[]> => {
    const project = this.projectName() || undefined;
    return this.api.searchNodes(query, project, undefined, 1, 25).pipe(
      map(r => r.items.map(n => ({
        value: n.qualifiedName || n.name,
        label: n.name,
        description: n.qualifiedName !== n.name ? n.qualifiedName : undefined,
        icon: this.labelIcons[n.label] || ''
      })))
    );
  };

  ngOnInit() {
    // Read query params to support linking: /impact?project=X&node=Y
    this.route.queryParams
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => {
        if (params['node']) this.nodeName.set(params['node']);
        if (params['project']) this.projectName.set(params['project']);
        if (params['depth']) this.depth.set(+params['depth']);
        if (params['node'] && params['project']) this.analyze();
      });
  }

  analyze() {
    const node = this.nodeName().trim();
    const project = this.projectName().trim();
    if (!node || !project) return;

    this.loading.set(true);
    this.error.set(null);
    this.searched.set(true);
    this.selectedNodeId.set(null);

    this.api.getImpact(project, node, this.depth()).subscribe({
      next: report => {
        this.report.set(report);
        this.loading.set(false);
        const first = report.affectedNodes[0];
        if (first) this.selectedNodeId.set(first.nodeId);
      },
      error: err => {
        this.error.set(err.status === 404 ? 'No matching nodes found.' : 'Analysis failed.');
        this.report.set(null);
        this.loading.set(false);
      }
    });
  }

  setDepth(d: number) {
    if (d === this.depth()) return;
    this.depth.set(d);
    if (this.nodeName() && this.projectName()) this.analyze();
  }

  select(nodeId: number) {
    this.selectedNodeId.set(nodeId);
  }

  isCrossRepo(n: AffectedNode): boolean {
    const primary = this.primaryChanged();
    return !!primary && n.project !== primary.project;
  }

  riskPillLabel(risk: RiskLevel): string {
    switch (risk) {
      case 'Critical': return 'crit';
      case 'High':     return 'high';
      case 'Medium':   return 'med';
      case 'Low':      return 'low';
    }
  }

  /** Returns array of indent characters for the tree row. Last is `└`, others blank. */
  indentCharacters(depth: number): string[] {
    if (depth <= 0) return [];
    return Array.from({ length: depth }, (_, i) => i === depth - 1 ? '└' : ' ');
  }

  onProjectSelected(value: string) {
    this.projectName.set(value);
  }

  onNodeSelected(value: string) {
    this.nodeName.set(value);
  }

  onKeydown(event: KeyboardEvent) {
    if (event.key === 'Enter') this.analyze();
  }

  riskCount(risk: RiskLevel): number {
    const s = this.report()?.summary;
    if (!s) return 0;
    const key = `${risk.toLowerCase()}Count` as keyof ImpactSummary;
    return (s[key] as number) ?? 0;
  }

  icon(label: string): string {
    return this.labelIcons[label] || '';
  }
}
