import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import {
  ProjectDetailResponse,
  ProjectHealthResponse,
  ProjectSecurityResponse,
  AnalysisBatchStatus,
  LABEL_ICONS,
  CONFIDENCE_COLORS,
  DotnetSupportInfo,
  ProjectDiagnosticsResponse,
  ProjectReviewResponse,
  ProjectReviewRunResponse,
  ProjectDiagnosticResponse,
  ProjectReviewFindingResponse
} from '../../core/models';
import { marked } from 'marked';

interface ProjectReviewPanelState {
  latestReview: ProjectReviewResponse | null;
  diagnostics: ProjectDiagnosticsResponse | null;
  diagnosticsPreview: ProjectDiagnosticResponse[];
  loadingLatestReview: boolean;
  loadingDiagnostics: boolean;
  startingReview: boolean;
  activeRun: ProjectReviewRunResponse | null;
  streamStatus: string | null;
  streamError: string | null;
  diagnosticsOpen: boolean;
}

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
  projectReviewStates = signal<Record<string, ProjectReviewPanelState>>({});

  readonly labelIcons = LABEL_ICONS;
  readonly confidenceColors = CONFIDENCE_COLORS;
  private readonly diagnosticPreviewLimit = 20;
  private readonly reviewStreamControllers = new Map<string, AbortController>();

  constructor() {
    this.destroyRef.onDestroy(() => this.abortReviewStreams());
  }

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
    this.abortReviewStreams();
    this.projectReviewStates.set({});

    this.api.getProject(n).subscribe({
      next: d => {
        this.detail.set(d);
        this.loading.set(false);
        this.initializeProjectReviewStates(n, d);
      },
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

  private createInitialReviewState(): ProjectReviewPanelState {
    return {
      latestReview: null,
      diagnostics: null,
      diagnosticsPreview: [],
      loadingLatestReview: false,
      loadingDiagnostics: false,
      startingReview: false,
      activeRun: null,
      streamStatus: null,
      streamError: null,
      diagnosticsOpen: false
    };
  }

  private initializeProjectReviewStates(repo: string, detail: ProjectDetailResponse) {
    const projectNames = Array.from(new Set(detail.analyses.map(analysis => analysis.projectName)));
    const states = Object.fromEntries(projectNames.map(projectName => [projectName, this.createInitialReviewState()]));
    this.projectReviewStates.set(states);

    for (const projectName of projectNames) {
      this.loadLatestProjectReview(repo, projectName);
      this.loadProjectDiagnostics(repo, projectName);
    }
  }

  private updateProjectReviewState(
    projectName: string,
    updater: (state: ProjectReviewPanelState) => ProjectReviewPanelState) {
    this.projectReviewStates.update(states => ({
      ...states,
      [projectName]: updater(states[projectName] ?? this.createInitialReviewState())
    }));
  }

  reviewState(projectName: string): ProjectReviewPanelState {
    return this.projectReviewStates()[projectName] ?? this.createInitialReviewState();
  }

  completedReview(projectName: string): ProjectReviewResponse | null {
    const review = this.reviewState(projectName).latestReview;
    return review && review.run.status.toLowerCase() === 'completed' ? review : null;
  }

  isReviewInProgress(projectName: string): boolean {
    const status = this.reviewState(projectName).activeRun?.status?.toLowerCase();
    return status === 'queued' || status === 'running';
  }

  reviewActionLabel(projectName: string): string {
    const state = this.reviewState(projectName);
    if (state.startingReview || this.isReviewInProgress(projectName)) return 'Reviewing…';
    return state.latestReview ? 'Re-run Review' : 'Generate Review';
  }

  reviewStatusTone(status?: string): string {
    switch (status?.toLowerCase()) {
      case 'queued':
        return 'queued';
      case 'running':
        return 'running';
      case 'completed':
        return 'completed';
      case 'failed':
        return 'failed';
      default:
        return 'idle';
    }
  }

  reviewStatusLabel(status?: string): string {
    switch (status?.toLowerCase()) {
      case 'queued':
        return 'Queued';
      case 'running':
        return 'Running';
      case 'completed':
        return 'Completed';
      case 'failed':
        return 'Failed';
      default:
        return 'Idle';
    }
  }

  private loadLatestProjectReview(repo: string, projectName: string) {
    this.updateProjectReviewState(projectName, state => ({
      ...state,
      loadingLatestReview: true,
      streamError: null
    }));

    this.api.getLatestProjectReview(repo, projectName).subscribe({
      next: review => {
        if (this.name() !== repo) return;

        const status = review.run.status.toLowerCase();
        this.updateProjectReviewState(projectName, state => ({
          ...state,
          latestReview: review,
          loadingLatestReview: false,
          activeRun: status === 'queued' || status === 'running' ? review.run : null,
          streamStatus: status === 'queued' || status === 'running'
            ? this.defaultReviewStatusMessage(review.run.status)
            : null,
          streamError: status === 'failed'
            ? (review.run.error ?? 'The latest review failed.')
            : null
        }));

        if (status === 'queued' || status === 'running') {
          this.connectReviewStream(repo, projectName, review.run.id);
        }
      },
      error: err => {
        if (this.name() !== repo) return;

        this.updateProjectReviewState(projectName, state => ({
          ...state,
          loadingLatestReview: false,
          streamError: err?.status === 404 ? null : 'Unable to load the latest review right now.'
        }));
      }
    });
  }

  private loadProjectDiagnostics(repo: string, projectName: string) {
    this.updateProjectReviewState(projectName, state => ({
      ...state,
      loadingDiagnostics: true
    }));

    this.api.getProjectDiagnostics(repo, projectName).subscribe({
      next: diagnostics => {
        if (this.name() !== repo) return;

        this.updateProjectReviewState(projectName, state => ({
          ...state,
          diagnostics,
          diagnosticsPreview: this.buildDiagnosticsPreview(diagnostics),
          loadingDiagnostics: false
        }));
      },
      error: () => {
        if (this.name() !== repo) return;

        this.updateProjectReviewState(projectName, state => ({
          ...state,
          diagnostics: null,
          diagnosticsPreview: [],
          loadingDiagnostics: false
        }));
      }
    });
  }

  startReview(event: MouseEvent, projectName: string) {
    event.stopPropagation();
    if (this.isReviewInProgress(projectName)) return;

    const repo = this.name();
    this.updateProjectReviewState(projectName, state => ({
      ...state,
      startingReview: true,
      streamError: null,
      streamStatus: 'Submitting review request…'
    }));

    this.api.startProjectReview(repo, projectName).subscribe({
      next: response => {
        if (this.name() !== repo) return;

        const now = new Date().toISOString();
        this.updateProjectReviewState(projectName, state => ({
          ...state,
          startingReview: false,
          activeRun: {
            id: response.reviewRunId,
            project: repo,
            projectName,
            reviewedCommitSha: undefined,
            status: response.status,
            reviewMode: 'standard',
            promptVersion: 'v1',
            modelUsed: undefined,
            createdAt: now,
            startedAt: undefined,
            completedAt: undefined,
            error: undefined
          },
          streamStatus: this.defaultReviewStatusMessage(response.status),
          streamError: null
        }));

        this.connectReviewStream(repo, projectName, response.reviewRunId);
      },
      error: () => {
        if (this.name() !== repo) return;

        this.updateProjectReviewState(projectName, state => ({
          ...state,
          startingReview: false,
          streamStatus: null,
          streamError: 'Unable to start a review right now.'
        }));
      }
    });
  }

  private connectReviewStream(repo: string, projectName: string, reviewRunId: number) {
    this.abortReviewStream(projectName);

    const controller = new AbortController();
    this.reviewStreamControllers.set(projectName, controller);
    void this.consumeReviewStream(repo, projectName, reviewRunId, controller);
  }

  private async consumeReviewStream(
    repo: string,
    projectName: string,
    reviewRunId: number,
    controller: AbortController) {
    try {
      for await (const event of this.api.streamProjectReview(repo, reviewRunId, controller.signal)) {
        if (controller.signal.aborted || this.name() !== repo) return;

        switch (event.type) {
          case 'status':
            this.updateProjectReviewState(projectName, state => ({
              ...state,
              startingReview: false,
              activeRun: this.mergeActiveRun(
                state.activeRun,
                repo,
                projectName,
                reviewRunId,
                {
                  status: event.content.status,
                  startedAt: event.content.startedAt,
                  completedAt: event.content.completedAt,
                  error: event.content.error
                }),
              streamStatus: this.defaultReviewStatusMessage(event.content.status),
              streamError: event.content.error ?? null
            }));
            break;
          case 'progress':
            this.updateProjectReviewState(projectName, state => ({
              ...state,
              startingReview: false,
              streamStatus: event.content.message,
              activeRun: this.mergeActiveRun(
                state.activeRun,
                repo,
                projectName,
                reviewRunId,
                { status: event.content.status })
            }));
            break;
          case 'finding':
            this.updateProjectReviewState(projectName, state => ({
              ...state,
              streamStatus: `Review found: ${event.content.title}`
            }));
            break;
          case 'completed':
            this.updateProjectReviewState(projectName, state => ({
              ...state,
              latestReview: event.content,
              activeRun: null,
              startingReview: false,
              streamStatus: null,
              streamError: null
            }));
            this.loadProjectDiagnostics(repo, projectName);
            return;
          case 'error':
            this.updateProjectReviewState(projectName, state => ({
              ...state,
              activeRun: state.activeRun
                ? { ...state.activeRun, status: event.content.status ?? 'failed', error: event.content.message }
                : null,
              startingReview: false,
              streamStatus: null,
              streamError: event.content.message
            }));
            this.loadLatestProjectReview(repo, projectName);
            return;
        }
      }
    } catch (err: any) {
      if (controller.signal.aborted || this.name() !== repo || err?.name === 'AbortError') return;

      this.updateProjectReviewState(projectName, state => ({
        ...state,
        activeRun: null,
        startingReview: false,
        streamStatus: null,
        streamError: 'Lost the live review stream. Refresh to check the latest saved result.'
      }));
    } finally {
      if (this.reviewStreamControllers.get(projectName) === controller) {
        this.reviewStreamControllers.delete(projectName);
      }
    }
  }

  private abortReviewStream(projectName: string) {
    const controller = this.reviewStreamControllers.get(projectName);
    if (!controller) return;
    controller.abort();
    this.reviewStreamControllers.delete(projectName);
  }

  private abortReviewStreams() {
    for (const controller of this.reviewStreamControllers.values()) {
      controller.abort();
    }
    this.reviewStreamControllers.clear();
  }

  toggleDiagnostics(projectName: string) {
    this.updateProjectReviewState(projectName, state => ({
      ...state,
      diagnosticsOpen: !state.diagnosticsOpen
    }));
  }

  totalDiagnostics(diagnostics?: ProjectDiagnosticsResponse | null): number {
    return diagnostics ? diagnostics.errorCount + diagnostics.warningCount + diagnostics.infoCount : 0;
  }

  diagnosticPreview(projectName: string): ProjectDiagnosticResponse[] {
    return this.reviewState(projectName).diagnosticsPreview;
  }

  hasMoreDiagnostics(projectName: string): boolean {
    const state = this.reviewState(projectName);
    return !!state.diagnostics && state.diagnostics.diagnostics.length > state.diagnosticsPreview.length;
  }

  reviewFindingTrackBy(index: number, finding: ProjectReviewFindingResponse): string {
    return `${finding.filePath}:${finding.lineStart ?? 0}:${index}`;
  }

  diagnosticTrackBy(index: number, diagnostic: ProjectDiagnosticResponse): string {
    return `${diagnostic.filePath}:${diagnostic.lineStart ?? 0}:${diagnostic.diagnosticId}:${index}`;
  }

  diagnosticSeverityColor(severity: string): string {
    switch (severity.toLowerCase()) {
      case 'error':
        return '#dc2626';
      case 'warning':
        return '#d97706';
      default:
        return '#2563eb';
    }
  }

  reviewConfidenceColor(confidence: string): string {
    switch (confidence.toLowerCase()) {
      case 'high':
        return '#15803d';
      case 'medium':
        return '#b45309';
      default:
        return '#6b7280';
    }
  }

  private diagnosticSeverityRank(severity: string): number {
    switch (severity.toLowerCase()) {
      case 'error':
        return 0;
      case 'warning':
        return 1;
      default:
        return 2;
    }
  }

  private buildDiagnosticsPreview(diagnostics: ProjectDiagnosticsResponse): ProjectDiagnosticResponse[] {
    return [...diagnostics.diagnostics]
      .sort((a, b) => this.diagnosticSeverityRank(a.severity) - this.diagnosticSeverityRank(b.severity))
      .slice(0, this.diagnosticPreviewLimit);
  }

  private mergeActiveRun(
    existing: ProjectReviewRunResponse | null,
    repo: string,
    projectName: string,
    reviewRunId: number,
    updates: Partial<ProjectReviewRunResponse>): ProjectReviewRunResponse {
    return {
      id: reviewRunId,
      project: repo,
      projectName,
      reviewedCommitSha: existing?.reviewedCommitSha,
      status: existing?.status ?? 'queued',
      reviewMode: existing?.reviewMode ?? 'standard',
      promptVersion: existing?.promptVersion ?? 'v1',
      modelUsed: existing?.modelUsed,
      createdAt: existing?.createdAt ?? new Date().toISOString(),
      startedAt: existing?.startedAt,
      completedAt: existing?.completedAt,
      error: existing?.error,
      ...updates
    };
  }

  private defaultReviewStatusMessage(status?: string): string | null {
    switch (status?.toLowerCase()) {
      case 'queued':
        return 'Queued for server-side review execution.';
      case 'running':
        return 'Review is gathering evidence and synthesizing findings.';
      default:
        return null;
    }
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

  formatDateTime(d?: string) {
    return d ? new Date(d).toLocaleString() : '—';
  }

  formatLineRange(lineStart?: number, lineEnd?: number): string {
    if (!lineStart) return '';
    return lineEnd && lineEnd !== lineStart ? `:${lineStart}-${lineEnd}` : `:${lineStart}`;
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
