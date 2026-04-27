import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked } from 'marked';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import {
  AnalysisBatchStatus,
  CONFIDENCE_COLORS,
  DotnetSupportInfo,
  LABEL_ICONS,
  MonthlyCommitPoint,
  ProjectDetailResponse,
  ProjectDiagnosticResponse,
  ProjectDiagnosticsResponse,
  ProjectHealthResponse,
  ProjectSecurityResponse,
  RepositoryVitalitySummary,
  RepositoryReviewFindingResponse,
  RepositoryReviewProjectSectionResponse,
  RepositoryReviewResponse,
  RepositoryReviewRunResponse,
  StoredProjectAnalysis
} from '../../core/models';

interface ProjectDiagnosticsPanelState {
  diagnostics: ProjectDiagnosticsResponse | null;
  diagnosticsPreview: ProjectDiagnosticResponse[];
  loadingDiagnostics: boolean;
  diagnosticsOpen: boolean;
}

interface RepositoryReviewPanelState {
  latestReview: RepositoryReviewResponse | null;
  loadingLatestReview: boolean;
  startingReview: boolean;
  activeRun: RepositoryReviewRunResponse | null;
  streamStatus: string | null;
  streamError: string | null;
  expanded: boolean;
}

interface RepoProjectEntry {
  projectName: string;
  analysis: StoredProjectAnalysis | null;
}

@Component({
  selector: 'app-repo-detail',
  imports: [RouterLink],
  templateUrl: './repo-detail.component.html',
  styleUrl: './repo-detail.component.scss'
})
export class RepoDetailComponent implements OnInit {
  private static readonly repositoryReviewRunStoragePrefix = 'codegraph:repository-review-run:';

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
  repositoryReviewState = signal<RepositoryReviewPanelState>(this.createInitialRepositoryReviewState());
  projectDiagnosticsStates = signal<Record<string, ProjectDiagnosticsPanelState>>({});

  readonly labelIcons = LABEL_ICONS;
  readonly confidenceColors = CONFIDENCE_COLORS;
  private readonly diagnosticPreviewLimit = 20;
  private reviewStreamController: AbortController | null = null;
  private loadRequestId = 0;

  constructor() {
    this.destroyRef.onDestroy(() => this.abortRepositoryReviewStream());
  }

  ngOnInit() {
    this.route.paramMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => this.loadProject(params.get('name') ?? ''));
  }

  private loadProject(n: string) {
    const requestId = ++this.loadRequestId;
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
    this.abortRepositoryReviewStream();
    this.repositoryReviewState.set(this.createInitialRepositoryReviewState());
    this.projectDiagnosticsStates.set({});

    this.api.getProject(n).subscribe({
      next: d => {
        if (requestId !== this.loadRequestId) return;
        this.detail.set(d);
        this.loading.set(false);
        this.initializeProjectDiagnosticsStates(n, d);
        this.loadLatestRepositoryReview(n);
      },
      error: () => {
        if (requestId !== this.loadRequestId) return;
        this.loading.set(false);
      }
    });
    this.api.getProjectHealth(n).subscribe({
      next: h => {
        if (requestId !== this.loadRequestId) return;
        this.health.set(h);
      },
      error: () => {}
    });
    this.api.getProjectSecurity(n).subscribe({
      next: s => {
        if (requestId !== this.loadRequestId) return;
        this.security.set(s);
      },
      error: () => {}
    });
    this.api.getProjectReadme(n).subscribe({
      next: r => {
        if (requestId !== this.loadRequestId) return;
        this.readme.set(r.content);
      },
      error: () => {}
    });
    this.api.getBatchStatus(n).subscribe({
      next: b => {
        if (requestId !== this.loadRequestId) return;
        this.batchStatus.set(b);
      },
      error: () => {}
    });
  }

  private createInitialProjectDiagnosticsState(): ProjectDiagnosticsPanelState {
    return {
      diagnostics: null,
      diagnosticsPreview: [],
      loadingDiagnostics: false,
      diagnosticsOpen: false
    };
  }

  private createInitialRepositoryReviewState(): RepositoryReviewPanelState {
    return {
      latestReview: null,
      loadingLatestReview: false,
      startingReview: false,
      activeRun: null,
      streamStatus: null,
      streamError: null,
      expanded: true
    };
  }

  private initializeProjectDiagnosticsStates(repo: string, detail: ProjectDetailResponse) {
    const projectNames = Array.from(new Set(detail.analyses.map(analysis => analysis.projectName)));
    const states = Object.fromEntries(
      projectNames.map(projectName => [projectName, this.createInitialProjectDiagnosticsState()])
    );
    this.projectDiagnosticsStates.set(states);

    for (const projectName of projectNames) {
      this.loadProjectDiagnostics(repo, projectName);
    }
  }

  private updateProjectDiagnosticsState(
    projectName: string,
    updater: (state: ProjectDiagnosticsPanelState) => ProjectDiagnosticsPanelState) {
    this.projectDiagnosticsStates.update(states => ({
      ...states,
      [projectName]: updater(states[projectName] ?? this.createInitialProjectDiagnosticsState())
    }));
  }

  private updateRepositoryReviewState(
    updater: (state: RepositoryReviewPanelState) => RepositoryReviewPanelState) {
    this.repositoryReviewState.update(updater);
  }

  projectDiagnosticsState(projectName: string): ProjectDiagnosticsPanelState {
    return this.projectDiagnosticsStates()[projectName] ?? this.createInitialProjectDiagnosticsState();
  }

  completedRepositoryReview(): RepositoryReviewResponse | null {
    const review = this.repositoryReviewState().latestReview;
    return review && review.run.status.toLowerCase() === 'completed' ? review : null;
  }

  isRepositoryReviewInProgress(): boolean {
    const status = this.repositoryReviewState().activeRun?.status?.toLowerCase();
    return status === 'queued' || status === 'running';
  }

  isRepositoryReviewStale(): boolean {
    const review = this.completedRepositoryReview();
    const currentCommit = this.detail()?.project.lastCommitSha;
    return !!review?.run.reviewedCommitSha &&
      !!currentCommit &&
      review.run.reviewedCommitSha !== currentCommit;
  }

  interruptedRepositoryReview(): RepositoryReviewResponse | null {
    const review = this.repositoryReviewState().latestReview;
    return review && review.run.status.toLowerCase() === 'interrupted' ? review : null;
  }

  canContinueInterruptedRepositoryReview(): boolean {
    const review = this.interruptedRepositoryReview();
    if (!review) return false;

    if (review.run.reviewMode.toLowerCase() !== 'update') return true;

    const currentCommit = this.detail()?.project.lastCommitSha;
    return !review.run.reviewedCommitSha ||
      !currentCommit ||
      review.run.reviewedCommitSha === currentCommit;
  }

  repositoryReviewActionLabel(): string {
    const state = this.repositoryReviewState();
    if (state.startingReview || this.isRepositoryReviewInProgress()) return 'Reviewing…';
    if (this.canContinueInterruptedRepositoryReview()) return 'Continue Review';
    if (!state.latestReview) return 'Run Code Review';
    if (this.isRepositoryReviewStale()) return 'Update Review';
    return 'Re-run Code Review';
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
      case 'interrupted':
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
      case 'interrupted':
        return 'Interrupted';
      default:
        return 'Idle';
    }
  }

  private loadLatestRepositoryReview(repo: string) {
    this.resumeTrackedRepositoryReview(repo);

    this.updateRepositoryReviewState(state => ({
      ...state,
      loadingLatestReview: true,
      streamError: null
    }));

    this.api.getLatestRepositoryReview(repo).subscribe({
      next: review => {
        if (this.name() !== repo) return;

        const status = review.run.status.toLowerCase();
        const isInProgress = status === 'queued' || status === 'running';
        const preserveTrackedRun = this.shouldPreserveTrackedRepositoryReview(
          this.repositoryReviewState().activeRun,
          review.run);
        this.updateRepositoryReviewState(state => ({
          ...state,
          activeRun: preserveTrackedRun
            ? state.activeRun
            : (isInProgress ? review.run : null),
          latestReview: review,
          loadingLatestReview: false,
          streamStatus: isInProgress
            ? this.defaultRepositoryReviewStatusMessage(review.run.status)
            : (preserveTrackedRun ? state.streamStatus : null),
          streamError: preserveTrackedRun
            ? state.streamError
            : status === 'failed'
            ? (review.run.error ?? 'The latest repository review failed.')
            : null,
          expanded: isInProgress || preserveTrackedRun
            ? true
            : state.expanded
        }));

        if (isInProgress) {
          this.trackRepositoryReviewRun(repo, review.run.id);
          this.connectRepositoryReviewStream(repo, review.run.id);
        } else {
          this.clearTrackedRepositoryReviewRun(repo, review.run.id);
        }
      },
      error: err => {
        if (this.name() !== repo) return;

        this.updateRepositoryReviewState(state => ({
          ...state,
          loadingLatestReview: false,
          streamError: err?.status === 404 ? null : 'Unable to load the latest repository review right now.'
        }));
      }
    });
  }

  private resumeTrackedRepositoryReview(repo: string) {
    const trackedReviewRunId = this.getTrackedRepositoryReviewRunId(repo);
    if (!trackedReviewRunId) return;

    this.api.getRepositoryReview(repo, trackedReviewRunId).subscribe({
      next: review => {
        if (this.name() !== repo) return;

        const status = review.run.status.toLowerCase();
        if (status === 'queued' || status === 'running') {
          this.trackRepositoryReviewRun(repo, review.run.id);
          this.updateRepositoryReviewState(state => ({
            ...state,
            activeRun: review.run,
            streamStatus: this.defaultRepositoryReviewStatusMessage(review.run.status),
            streamError: null,
            expanded: true
          }));
          this.connectRepositoryReviewStream(repo, review.run.id);
          return;
        }

        this.clearTrackedRepositoryReviewRun(repo, trackedReviewRunId);
      },
      error: () => {
        if (this.name() !== repo) return;
        this.clearTrackedRepositoryReviewRun(repo, trackedReviewRunId);
      }
    });
  }

  private loadProjectDiagnostics(repo: string, projectName: string) {
    this.updateProjectDiagnosticsState(projectName, state => ({
      ...state,
      loadingDiagnostics: true
    }));

    this.api.getProjectDiagnostics(repo, projectName).subscribe({
      next: diagnostics => {
        if (this.name() !== repo) return;

        this.updateProjectDiagnosticsState(projectName, state => ({
          ...state,
          diagnostics,
          diagnosticsPreview: this.buildDiagnosticsPreview(diagnostics),
          loadingDiagnostics: false
        }));
      },
      error: () => {
        if (this.name() !== repo) return;

        this.updateProjectDiagnosticsState(projectName, state => ({
          ...state,
          diagnostics: null,
          diagnosticsPreview: [],
          loadingDiagnostics: false
        }));
      }
    });
  }

  startRepositoryReview(mode?: 'full' | 'update') {
    if (this.isRepositoryReviewInProgress()) return;

    const repo = this.name();
    const interruptedReview = this.interruptedRepositoryReview();
    const effectiveMode = mode ??
      (this.canContinueInterruptedRepositoryReview()
        ? (interruptedReview?.run.reviewMode.toLowerCase() === 'update' ? 'update' : 'full')
        : (this.isRepositoryReviewStale() ? 'update' : 'full'));
    this.updateRepositoryReviewState(state => ({
      ...state,
      startingReview: true,
      streamError: null,
      streamStatus: 'Submitting repository review request…',
      expanded: true
    }));

    this.api.startRepositoryReview(repo, effectiveMode).subscribe({
      next: response => {
        if (this.name() !== repo) return;

        const now = new Date().toISOString();
        this.updateRepositoryReviewState(state => ({
          ...state,
          startingReview: false,
          activeRun: {
            id: response.reviewRunId,
            repo,
            reviewedCommitSha: undefined,
            baselineReviewRunId: state.latestReview?.run.id,
            baselineCommitSha: state.latestReview?.run.reviewedCommitSha,
            status: response.status,
            reviewMode: effectiveMode,
            promptVersion: 'v1',
            modelUsed: undefined,
            createdAt: now,
            startedAt: undefined,
            completedAt: undefined,
            error: undefined
          },
          streamStatus: this.defaultRepositoryReviewStatusMessage(response.status),
          streamError: null,
          expanded: true
        }));

        this.trackRepositoryReviewRun(repo, response.reviewRunId);
        this.connectRepositoryReviewStream(repo, response.reviewRunId);
      },
      error: () => {
        if (this.name() !== repo) return;

        this.updateRepositoryReviewState(state => ({
          ...state,
          startingReview: false,
          streamStatus: null,
          streamError: 'Unable to start a repository review right now.'
        }));
      }
    });
  }

  private connectRepositoryReviewStream(repo: string, reviewRunId: number) {
    this.abortRepositoryReviewStream();

    const controller = new AbortController();
    this.reviewStreamController = controller;
    void this.consumeRepositoryReviewStream(repo, reviewRunId, controller);
  }

  private async consumeRepositoryReviewStream(
    repo: string,
    reviewRunId: number,
    controller: AbortController) {
    try {
      for await (const event of this.api.streamRepositoryReview(repo, reviewRunId, controller.signal)) {
        if (controller.signal.aborted || this.name() !== repo) return;

        switch (event.type) {
          case 'status':
            this.updateRepositoryReviewState(state => ({
              ...state,
              startingReview: false,
              activeRun: this.mergeActiveRepositoryRun(
                state.activeRun,
                repo,
                reviewRunId,
                {
                  status: event.content.status,
                  startedAt: event.content.startedAt,
                  completedAt: event.content.completedAt,
                  error: event.content.error
                }),
              streamStatus: this.defaultRepositoryReviewStatusMessage(event.content.status),
              streamError: event.content.error ?? null
            }));
            break;
          case 'progress':
            this.updateRepositoryReviewState(state => ({
              ...state,
              startingReview: false,
              streamStatus: event.content.message,
              activeRun: this.mergeActiveRepositoryRun(
                state.activeRun,
                repo,
                reviewRunId,
                { status: event.content.status })
            }));
            break;
          case 'finding':
            this.updateRepositoryReviewState(state => ({
              ...state,
              streamStatus: `Review found: ${event.content.title}`
            }));
            break;
          case 'completed':
            this.clearTrackedRepositoryReviewRun(repo, reviewRunId);
            this.updateRepositoryReviewState(state => ({
              ...state,
              latestReview: event.content,
              activeRun: null,
              startingReview: false,
              streamStatus: null,
              streamError: null,
              expanded: true
            }));
            return;
          case 'error':
            this.clearTrackedRepositoryReviewRun(repo, reviewRunId);
            this.updateRepositoryReviewState(state => ({
              ...state,
              activeRun: state.activeRun
                ? { ...state.activeRun, status: event.content.status ?? 'failed', error: event.content.message }
                : null,
              startingReview: false,
              streamStatus: null,
              streamError: event.content.message
            }));
            this.loadLatestRepositoryReview(repo);
            return;
        }
      }
    } catch (err: any) {
      if (controller.signal.aborted || this.name() !== repo || err?.name === 'AbortError') return;

      this.updateRepositoryReviewState(state => ({
        ...state,
        activeRun: null,
        startingReview: false,
        streamStatus: null,
        streamError: 'Lost the live repository review stream. Refresh to check the latest saved result.'
      }));
    } finally {
      if (this.reviewStreamController === controller) {
        this.reviewStreamController = null;
      }
    }
  }

  private abortRepositoryReviewStream() {
    if (!this.reviewStreamController) return;
    this.reviewStreamController.abort();
    this.reviewStreamController = null;
  }

  toggleCodeReviewExpanded() {
    this.updateRepositoryReviewState(state => ({
      ...state,
      expanded: !state.expanded
    }));
  }

  toggleDiagnostics(projectName: string) {
    this.updateProjectDiagnosticsState(projectName, state => ({
      ...state,
      diagnosticsOpen: !state.diagnosticsOpen
    }));
  }

  totalDiagnostics(diagnostics?: ProjectDiagnosticsResponse | null): number {
    return diagnostics ? diagnostics.errorCount + diagnostics.warningCount + diagnostics.infoCount : 0;
  }

  diagnosticPreview(projectName: string): ProjectDiagnosticResponse[] {
    return this.projectDiagnosticsState(projectName).diagnosticsPreview;
  }

  hasMoreDiagnostics(projectName: string): boolean {
    const state = this.projectDiagnosticsState(projectName);
    return !!state.diagnostics && state.diagnostics.diagnostics.length > state.diagnosticsPreview.length;
  }

  reviewFindingTrackBy(index: number, finding: RepositoryReviewFindingResponse): string {
    return `${finding.projectName ?? 'repo'}:${finding.filePath}:${finding.lineStart ?? 0}:${index}`;
  }

  projectSectionTrackBy(index: number, section: RepositoryReviewProjectSectionResponse): string {
    return `${section.projectName}:${index}`;
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

  private mergeActiveRepositoryRun(
    existing: RepositoryReviewRunResponse | null,
    repo: string,
    reviewRunId: number,
    updates: Partial<RepositoryReviewRunResponse>): RepositoryReviewRunResponse {
    return {
      id: reviewRunId,
      repo,
      reviewedCommitSha: existing?.reviewedCommitSha,
      baselineReviewRunId: existing?.baselineReviewRunId,
      baselineCommitSha: existing?.baselineCommitSha,
      status: existing?.status ?? 'queued',
      reviewMode: existing?.reviewMode ?? 'full',
      promptVersion: existing?.promptVersion ?? 'v1',
      modelUsed: existing?.modelUsed,
      createdAt: existing?.createdAt ?? new Date().toISOString(),
      startedAt: existing?.startedAt,
      completedAt: existing?.completedAt,
      error: existing?.error,
      ...updates
    };
  }

  private defaultRepositoryReviewStatusMessage(status?: string): string | null {
    switch (status?.toLowerCase()) {
      case 'queued':
        return 'Queued for server-side repository review execution.';
      case 'running':
        return 'Repository review is gathering project evidence and synthesizing results.';
      case 'interrupted':
        return 'The previous repository review was interrupted before it finished.';
      default:
        return null;
    }
  }

  private shouldPreserveTrackedRepositoryReview(
    activeRun: RepositoryReviewRunResponse | null,
    latestRun: RepositoryReviewRunResponse): boolean {
    if (!activeRun || activeRun.id === latestRun.id) return false;

    const activeStatus = activeRun.status?.toLowerCase();
    const latestStatus = latestRun.status?.toLowerCase();
    return (activeStatus === 'queued' || activeStatus === 'running') &&
      latestStatus !== 'queued' &&
      latestStatus !== 'running';
  }

  private repositoryReviewRunStorageKey(repo: string): string {
    return `${RepoDetailComponent.repositoryReviewRunStoragePrefix}${repo.toLowerCase()}`;
  }

  private getTrackedRepositoryReviewRunId(repo: string): number | null {
    try {
      const raw = window.localStorage.getItem(this.repositoryReviewRunStorageKey(repo));
      if (!raw) return null;

      const reviewRunId = Number.parseInt(raw, 10);
      if (Number.isFinite(reviewRunId) && reviewRunId > 0) {
        return reviewRunId;
      }

      this.clearTrackedRepositoryReviewRun(repo);
    } catch {
      return null;
    }

    return null;
  }

  private trackRepositoryReviewRun(repo: string, reviewRunId: number) {
    try {
      window.localStorage.setItem(this.repositoryReviewRunStorageKey(repo), reviewRunId.toString());
    } catch {
      // Ignore storage failures and keep the in-memory live state.
    }
  }

  private clearTrackedRepositoryReviewRun(repo: string, reviewRunId?: number) {
    try {
      const key = this.repositoryReviewRunStorageKey(repo);
      if (reviewRunId !== undefined) {
        const raw = window.localStorage.getItem(key);
        if (raw && Number.parseInt(raw, 10) !== reviewRunId) {
          return;
        }
      }

      window.localStorage.removeItem(key);
    } catch {
      // Ignore storage failures and keep the in-memory live state.
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
    if (score < 0.25) return '#ef4444';
    if (score < 0.50) return '#f59e0b';
    if (score < 0.75) return '#3b82f6';
    return '#22c55e';
  }

  healthLabel(score: number): string {
    if (score < 2.5) return 'CRITICAL';
    if (score < 4.0) return 'ALERT';
    if (score < 6.0) return 'WARNING';
    return 'HEALTHY';
  }

  concernTone(score: number): string {
    if (score >= 20) return '#b91c1c';
    if (score >= 12) return '#d97706';
    if (score >= 6) return '#2563eb';
    return '#6b7280';
  }

  vitality(): RepositoryVitalitySummary | null {
    return this.health()?.repositoryVitality ?? null;
  }

  hasVitalityChart(vitality?: RepositoryVitalitySummary | null): boolean {
    return !!vitality?.monthlyCommits?.length;
  }

  vitalityChartMax(vitality?: RepositoryVitalitySummary | null): number {
    const values = vitality?.monthlyCommits?.map(point => point.commitCount) ?? [];
    return Math.max(1, ...values);
  }

  vitalityBarHeight(point: MonthlyCommitPoint, vitality?: RepositoryVitalitySummary | null): number {
    const max = this.vitalityChartMax(vitality);
    return Math.max(10, Math.round((point.commitCount / max) * 88));
  }

  vitalityBarTone(point: MonthlyCommitPoint): string {
    if (point.commitCount === 0) return '#d1d5db';
    if (point.commitCount <= 2) return '#93c5fd';
    if (point.commitCount <= 6) return '#60a5fa';
    return '#2563eb';
  }

  vitalityStatusTone(status?: string): string {
    switch ((status ?? '').toLowerCase()) {
      case 'active':
      case 'stable':
      case 'revived':
        return '#15803d';
      case 'slowing':
        return '#b45309';
      case 'dormant':
      case 'possiblyabandoned':
        return '#b91c1c';
      default:
        return '#6b7280';
    }
  }

  vitalityStatusBackground(status?: string): string {
    switch ((status ?? '').toLowerCase()) {
      case 'active':
      case 'stable':
      case 'revived':
        return '#dcfce7';
      case 'slowing':
        return '#fef3c7';
      case 'dormant':
      case 'possiblyabandoned':
        return '#fee2e2';
      default:
        return '#f3f4f6';
    }
  }

  formatPercent(value?: number): string {
    if (value === undefined || value === null) return '0%';
    return `${(value * 100).toFixed(0)}%`;
  }

  formatSignedPercent(value?: number): string {
    if (value === undefined || value === null) return '0.0%';
    return `${value > 0 ? '+' : ''}${value.toFixed(1)}%`;
  }

  historyMaturityLabel(value?: string): string {
    if (!value) return 'Unknown';
    return value.charAt(0).toUpperCase() + value.slice(1).toLowerCase();
  }

  hotspotBadgeLabel(file: { bugFixCommits365d: number; recurringChurnScore: number }): string | null {
    if (file.bugFixCommits365d >= 1 && file.recurringChurnScore >= 0.5) return 'Repeated fixes';
    if (file.bugFixCommits365d >= 1) return 'Fix-heavy';
    if (file.recurringChurnScore >= 0.5) return 'Persistent churn';
    return null;
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

  projectEntries(): RepoProjectEntry[] {
    const detail = this.detail();
    if (!detail) return [];

    const analysesByProject = new Map(
      detail.analyses.map(analysis => [analysis.projectName, analysis] as const)
    );

    const orderedNames = [
      ...detail.analyses.map(analysis => analysis.projectName),
      ...Object.keys(detail.dotnetProjects ?? {})
    ];

    return Array.from(new Set(orderedNames))
      .filter(projectName => !!projectName)
      .map(projectName => ({
        projectName,
        analysis: analysesByProject.get(projectName) ?? null
      }));
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
    }
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
