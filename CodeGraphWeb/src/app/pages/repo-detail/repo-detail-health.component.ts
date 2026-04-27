import { Component, inject, Input, OnChanges, signal, SimpleChanges } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import {
  CONFIDENCE_COLORS,
  DotnetSupportInfo,
  MonthlyCommitPoint,
  ProjectDetailResponse,
  ProjectHealthResponse,
  ProjectSecurityResponse,
  RepositoryVitalitySummary
} from '../../core/models';
import { MarkdownComponent } from '../../shared/markdown.component';

@Component({
  selector: 'app-repo-detail-health',
  standalone: true,
  imports: [MarkdownComponent],
  templateUrl: './repo-detail-health.component.html',
  styleUrl: './repo-detail-health.component.scss'
})
export class RepoDetailHealthComponent implements OnChanges {
  private api = inject(ApiService);
  private router = inject(Router);

  @Input() projectName = '';
  @Input() health: ProjectHealthResponse | null = null;
  @Input() security: ProjectSecurityResponse | null = null;
  @Input() detail: ProjectDetailResponse | null = null;

  healthDetailsOpen = signal(false);
  securityOpen = signal(false);

  readonly confidenceColors = CONFIDENCE_COLORS;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['projectName']) {
      this.healthDetailsOpen.set(false);
      this.securityOpen.set(false);
    }
  }

  toggleHealthDetails() {
    this.healthDetailsOpen.update(v => !v);
  }

  toggleSecurity() {
    this.securityOpen.update(v => !v);
  }

  hasExpandableContent(): boolean {
    const health = this.health;
    return !!health && (
      !!health.securitySummary ||
      health.topHotspots.length > 0 ||
      !!health.repositoryVitality ||
      health.analyses.length > 0 ||
      health.projectHealths.length > 0
    );
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
    return this.security?.findings.filter(f => f.category === category) ?? [];
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

  dotnetSupport(): DotnetSupportInfo | null {
    return this.health?.dotnetSupport ?? this.detail?.dotnetSupport ?? null;
  }

  dotnetSupportLabel(status: string): string {
    switch (status) {
      case 'supported': return 'supported';
      case 'out_of_support': return 'out of support';
      case 'mixed': return 'mixed support';
      case 'os_lifecycle': return 'OS lifecycle';
      case 'not_applicable': return 'not applicable';
      default: return 'unknown';
    }
  }

  dotnetSupportColor(status: string): string {
    switch (status) {
      case 'supported': return '#15803d';
      case 'mixed': return '#b45309';
      case 'out_of_support': return '#b91c1c';
      case 'os_lifecycle': return '#475569';
      case 'not_applicable': return '#1d4ed8';
      default: return '#6b7280';
    }
  }

  dotnetSupportBackground(status: string): string {
    switch (status) {
      case 'supported': return '#dcfce7';
      case 'mixed': return '#fef3c7';
      case 'out_of_support': return '#fee2e2';
      case 'os_lifecycle': return '#e2e8f0';
      case 'not_applicable': return '#dbeafe';
      default: return '#f3f4f6';
    }
  }

  hasDotnetPenalty(): boolean {
    return (this.health?.repoHealth?.scorePenalty ?? 0) > 0;
  }

  formatDate(d?: string) {
    return d ? new Date(d).toLocaleDateString() : '-';
  }

  formatPenalty(value?: number) {
    return typeof value === 'number' ? value.toFixed(1) : '0.0';
  }

  vitalityLabel(value?: string | null): string {
    return value
      ? value.replace(/_/g, ' ').replace(/\b\w/g, char => char.toUpperCase())
      : 'Unknown';
  }

  vitalityChange(value: number): string {
    const prefix = value > 0 ? '+' : '';
    return `${prefix}${value.toFixed(1)}%`;
  }

  vitalityTrendTone(value: number): 'positive' | 'negative' | 'neutral' {
    if (value > 0) return 'positive';
    if (value < 0) return 'negative';
    return 'neutral';
  }

  vitalityTrendSummary(vitality: RepositoryVitalitySummary): string {
    if (!vitality.hasSufficientHistoryForTrends) {
      return 'Trend confidence is still immature, so recent activity changes are directional only.';
    }

    if (vitality.velocityChangePercent >= 20) {
      return 'Team activity is meaningfully higher than the prior six months.';
    }

    if (vitality.velocityChangePercent <= -20) {
      return 'Team activity has cooled versus the prior six months.';
    }

    return 'Team activity is broadly steady versus the prior six months.';
  }

  monthlyCommitHeight(point: MonthlyCommitPoint, vitality: RepositoryVitalitySummary): string {
    const max = Math.max(...vitality.monthlyCommits.map(item => item.commitCount), 0);
    if (max <= 0) return '8%';
    if (point.commitCount === 0) return '4%';
    return `${Math.max(12, Math.round((point.commitCount / max) * 100))}%`;
  }

  monthlyCommitLabel(monthStart: string): string {
    return new Date(monthStart).toLocaleDateString(undefined, { month: 'short' });
  }

  monthlyCommitTitle(monthStart: string): string {
    return new Date(monthStart).toLocaleDateString(undefined, {
      month: 'short',
      year: 'numeric'
    });
  }
}
