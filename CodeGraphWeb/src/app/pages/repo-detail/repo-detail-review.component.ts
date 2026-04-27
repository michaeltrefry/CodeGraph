import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { RepositoryReviewResponse } from '../../core/models';

const SEVERITY_COLORS: Record<string, string> = {
  critical: '#dc2626',
  high: '#ef4444',
  medium: '#f59e0b',
  low: '#6b7280'
};

const CATEGORY_LABELS: Record<string, string> = {
  bug: 'Bug',
  security: 'Security',
  reliability: 'Reliability',
  maintainability: 'Maintainability',
  readability: 'Readability',
  design: 'Design',
  'dead-code': 'Dead Code',
  'test-gap': 'Test Gap'
};

@Component({
  selector: 'app-repo-detail-review',
  standalone: true,
  templateUrl: './repo-detail-review.component.html',
  styleUrl: './repo-detail-review.component.scss'
})
export class RepoDetailReviewComponent implements OnChanges {
  private api = inject(ApiService);
  private router = inject(Router);

  @Input() projectName = '';
  @Input() review: RepositoryReviewResponse | null = null;
  @Input() reviewRunning = false;
  @Input() reviewLoading = false;
  @Input() lastCommitSha: string | null = null;

  @Output() startReview = new EventEmitter<void>();

  reviewOpen = signal(true);
  expandedReviewProject = signal<string | null>(null);

  readonly severityColors = SEVERITY_COLORS;
  readonly categoryLabels = CATEGORY_LABELS;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['projectName']) {
      this.reviewOpen.set(true);
      this.expandedReviewProject.set(null);
    }
  }

  toggleReview() {
    this.reviewOpen.update(v => !v);
  }

  toggleReviewProject(projectName: string) {
    this.expandedReviewProject.update(cur => cur === projectName ? null : projectName);
  }

  requestStartReview() {
    this.startReview.emit();
  }

  navigateToFinding(event: MouseEvent, filePath: string, lineNumber?: number) {
    const target = event.currentTarget as HTMLElement;
    target.classList.add('finding-location-loading');

    this.api.findNodeByFile(this.projectName, filePath, lineNumber ?? undefined).subscribe({
      next: res => {
        target.classList.remove('finding-location-loading');
        const queryParams = lineNumber ? { line: lineNumber } : {};
        this.router.navigate(['/nodes', res.nodeId], { queryParams });
      },
      error: () => {
        target.classList.remove('finding-location-loading');
        target.classList.add('finding-location-unavailable');
        target.title = 'File not indexed - no source view available';
      }
    });
  }

  isReviewStale(): boolean {
    const reviewedCommitSha = this.review?.run.reviewedCommitSha;
    if (!this.review || this.review.run.status !== 'completed' || !this.lastCommitSha || !reviewedCommitSha) {
      return false;
    }

    return this.lastCommitSha !== reviewedCommitSha;
  }

  reviewFindingsBySeverity(severity: string) {
    return this.review?.findings.filter(f => f.severity === severity) ?? [];
  }

  formatDate(d?: string) {
    return d ? new Date(d).toLocaleDateString() : '-';
  }

  formatCommit(sha?: string) {
    return sha ? sha.slice(0, 8) : '-';
  }
}
