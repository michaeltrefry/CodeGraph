import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import { ProjectDetailResponse, ProjectHealthResponse, ProjectSecurityResponse, AnalysisBatchStatus, LABEL_ICONS, CONFIDENCE_COLORS, DotnetSupportInfo } from '../../core/models';
import { marked } from 'marked';

@Component({
  selector: 'app-repo-detail',
  imports: [RouterLink],
  templateUrl: './repo-detail.component.html',
  styleUrl: './repo-detail.component.scss'
})
export class RepoDetailComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private sanitizer = inject(DomSanitizer);
  private destroyRef = inject(DestroyRef);
  private chatContext = inject(ChatContextService);
  private router = inject(Router);

  name = signal('');
  detail = signal<ProjectDetailResponse | null>(null);
  health = signal<ProjectHealthResponse | null>(null);
  readme = signal<string | null>(null);
  loading = signal(true);
  expandedAnalysis = signal<string | null>(null);
  healthOpen = signal(true);
  healthDetailsOpen = signal(false);
  expandedHealthAnalysis = signal<string | null>(null);
  security = signal<ProjectSecurityResponse | null>(null);
  securityOpen = signal(false);
  batchStatus = signal<AnalysisBatchStatus | null>(null);
  reAnalyzing = signal(false);
  deleting = signal(false);
  showDeleteConfirm = signal(false);

  readonly labelIcons = LABEL_ICONS;
  readonly confidenceColors = CONFIDENCE_COLORS;

  ngOnInit() {
    this.route.paramMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => this.loadProject(params.get('name') ?? ''));
  }

  private loadProject(n: string) {
    this.name.set(n);
    this.chatContext.setRepo(n);
    this.detail.set(null);
    this.health.set(null);
    this.readme.set(null);
    this.security.set(null);
    this.batchStatus.set(null);
    this.loading.set(true);
    this.expandedAnalysis.set(null);
    this.expandedHealthAnalysis.set(null);

    this.api.getProject(n).subscribe({
      next: d => { this.detail.set(d); this.loading.set(false); },
      error: () => this.loading.set(false)
    });
    this.api.getProjectHealth(n).subscribe({
      next: h => this.health.set(h),
      error: () => {}
    });
    this.api.getProjectSecurity(n).subscribe({
      next: s => this.security.set(s),
      error: () => {}
    });
    this.api.getProjectReadme(n).subscribe({
      next: r => this.readme.set(r.content),
      error: () => {}
    });
    this.api.getBatchStatus(n).subscribe({
      next: b => this.batchStatus.set(b),
      error: () => {}
    });
  }

  toggleHealth() {
    this.healthOpen.update(v => !v);
  }

  toggleHealthDetails() {
    this.healthDetailsOpen.update(v => !v);
  }

  toggleHealthAnalysis(key: string) {
    this.expandedHealthAnalysis.update(cur => cur === key ? null : key);
  }

  toggleSecurity() {
    this.securityOpen.update(v => !v);
  }

  severityColor(severity: string): string {
    switch (severity) {
      case 'critical': return '#dc2626';
      case 'high': return '#ef4444';
      case 'medium': return '#f59e0b';
      case 'low': return '#6b7280';
      default: return '#6b7280';
    }
  }

  securityFindings(category: string) {
    return this.security()?.findings.filter(f => f.category === category) ?? [];
  }

  navigateToFinding(event: MouseEvent, filePath: string, lineNumber?: number) {
    const target = event.currentTarget as HTMLElement;
    target.classList.add('finding-location-loading');

    this.api.findNodeByFile(this.name(), filePath, lineNumber ?? undefined).subscribe({
      next: res => {
        target.classList.remove('finding-location-loading');
        const queryParams = lineNumber ? { line: lineNumber } : {};
        this.router.navigate(['/nodes', res.nodeId], { queryParams });
      },
      error: () => {
        target.classList.remove('finding-location-loading');
        target.classList.add('finding-location-unavailable');
        target.title = 'File not indexed — no source view available';
      }
    });
  }

  healthColor(score: number): string {
    if (score < 4.0) return '#ef4444';
    if (score < 6.0) return '#f59e0b';
    return '#22c55e';
  }

  trustColor(score: number): string {
    if (score < 0.25) return '#ef4444';  // untrusted
    if (score < 0.50) return '#f59e0b';  // low
    if (score < 0.75) return '#3b82f6';  // medium
    return '#22c55e';                    // high
  }

  healthLabel(score: number): string {
    if (score < 2.5) return 'CRITICAL';
    if (score < 4.0) return 'ALERT';
    if (score < 6.0) return 'WARNING';
    return 'HEALTHY';
  }

  nodeLabelEntries() {
    const d = this.detail();
    if (!d) return [];
    return Object.entries(d.nodeCounts)
      .sort((a, b) => b[1] - a[1])
      .filter(([, count]) => count > 0);
  }

  dotnetProjectEntries() {
    const d = this.detail();
    if (!d?.dotnetProjects) return [];
    return Object.entries(d.dotnetProjects)
      .map(([name, counts]) => ({
        name,
        total: Object.values(counts).reduce((a, b) => a + b, 0),
        labels: Object.entries(counts).sort((a, b) => b[1] - a[1])
      }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }

  getDotnetProject(projectName: string) {
    return this.dotnetProjectEntries().find(p => p.name === projectName) ?? null;
  }

  toggleAnalysis(projectName: string) {
    this.expandedAnalysis.update(cur => cur === projectName ? null : projectName);
  }

  reAnalyze() {
    this.reAnalyzing.set(true);
    this.api.reAnalyze(this.name()).subscribe({
      next: b => {
        this.batchStatus.set(b);
        this.reAnalyzing.set(false);
      },
      error: () => this.reAnalyzing.set(false)
    });
  }

  confirmDelete() {
    this.showDeleteConfirm.set(true);
  }

  cancelDelete() {
    this.showDeleteConfirm.set(false);
  }

  deleteRepo() {
    this.deleting.set(true);
    this.api.deleteProject(this.name()).subscribe({
      next: () => this.router.navigate(['/repos']),
      error: () => this.deleting.set(false)
    });
  }

  isBatchInProgress(): boolean {
    const status = this.batchStatus()?.status?.toLowerCase();
    return !!status && status !== 'completed' && status !== 'failed';
  }

  batchProviderSummary(): string | null {
    const batch = this.batchStatus();
    if (!batch) return null;

    const parts = [
      batch.providerName,
      this.formatExecutionMode(batch.executionMode),
      batch.includeAllSource ? 'all source' : 'convention source'
    ];

    return parts.filter(Boolean).join(' · ');
  }

  private formatExecutionMode(mode?: string): string {
    if (!mode) return 'unknown mode';
    return mode.replace(/_/g, ' ');
  }

  formatDate(d?: string) {
    return d ? new Date(d).toLocaleDateString() : '—';
  }

  formatCommit(sha?: string) {
    return sha ? sha.slice(0, 8) : '—';
  }

  supportBadgeText(support?: DotnetSupportInfo | null): string | null {
    if (!support) return null;

    switch (support.overallStatus) {
      case 'supported':
        return 'supported';
      case 'out_of_support':
        return 'out of support';
      case 'mixed':
        return 'mixed support';
      default:
        return 'support unknown';
    };
  }

  supportBadgeColor(status?: string): string {
    switch (status) {
      case 'supported': return '#15803d';
      case 'out_of_support': return '#b91c1c';
      case 'mixed': return '#b45309';
      default: return '#6b7280';
    }
  }

  supportStatusLabel(status?: string): string {
    switch (status) {
      case 'supported': return 'Supported';
      case 'out_of_support': return 'Out of support';
      case 'mixed': return 'Mixed support';
      case 'not_applicable': return 'No independent lifecycle';
      case 'os_lifecycle': return 'Follows Windows lifecycle';
      default: return 'Unknown';
    }
  }

  renderMarkdown(text: string): SafeHtml {
    const html = marked.parse(text, { async: false }) as string;
    return this.sanitizer.bypassSecurityTrustHtml(html);
  }
}
