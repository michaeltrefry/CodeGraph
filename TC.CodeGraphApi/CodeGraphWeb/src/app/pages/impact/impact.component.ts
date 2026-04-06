import { Component, inject, signal, DestroyRef } from '@angular/core';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { UpperCasePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, map } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  ImpactReport, ImpactSummary, AffectedNode, RiskLevel,
  RISK_COLORS, RISK_BG_COLORS, LABEL_ICONS
} from '../../core/models';
import { TypeaheadComponent, TypeaheadItem } from '../../shared/typeahead.component';

@Component({
  selector: 'app-impact',
  imports: [RouterLink, FormsModule, UpperCasePipe, TypeaheadComponent],
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

  // Expand/collapse state for risk groups
  expandedGroups = signal<Set<RiskLevel>>(new Set(['Critical', 'High', 'Medium', 'Low']));

  readonly riskLevels: RiskLevel[] = ['Critical', 'High', 'Medium', 'Low'];
  readonly labelIcons = LABEL_ICONS;

  // Typeahead search functions
  searchProjects = (query: string): Observable<TypeaheadItem[]> =>
    this.api.listProjects(query, undefined, 1, 25).pipe(
      map(r => r.items.map(p => ({
        value: p.name,
        label: p.name,
        description: [p.gitLabGroup, p.framework].filter(Boolean).join(' / ')
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

    this.api.getImpact(project, node, this.depth()).subscribe({
      next: report => {
        this.report.set(report);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err.status === 404 ? 'No matching nodes found.' : 'Analysis failed.');
        this.report.set(null);
        this.loading.set(false);
      }
    });
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

  nodesForRisk(risk: RiskLevel): AffectedNode[] {
    return this.report()?.affectedNodes.filter(n => n.risk === risk) ?? [];
  }

  toggleGroup(risk: RiskLevel) {
    this.expandedGroups.update(set => {
      const next = new Set(set);
      next.has(risk) ? next.delete(risk) : next.add(risk);
      return next;
    });
  }

  isExpanded(risk: RiskLevel): boolean {
    return this.expandedGroups().has(risk);
  }

  riskCount(risk: RiskLevel): number {
    const s = this.report()?.summary;
    if (!s) return 0;
    const key = `${risk.toLowerCase()}Count` as keyof ImpactSummary;
    return (s[key] as number) ?? 0;
  }

  riskColor(risk: RiskLevel): string {
    return RISK_COLORS[risk] ?? '#6b7280';
  }

  riskBg(risk: RiskLevel): string {
    return RISK_BG_COLORS[risk] ?? '#f9fafb';
  }

  icon(label: string): string {
    return this.labelIcons[label] || '';
  }
}
