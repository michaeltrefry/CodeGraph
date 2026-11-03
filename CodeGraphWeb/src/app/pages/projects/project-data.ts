import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { RepositoryReviewFindingResponse, RepositoryReviewResponse, RepositoryReviewRunResponse } from '../../core/models';
import { KnowledgeApiService } from '../knowledge/knowledge-api.service';
import { ItemView, ProjectView, WorkStatus } from '../work/work.models';

/** The tabs of a project page, in order. The route's :tab is one of these ids. */
export const PROJECT_TABS = [
  { id: 'overview', label: 'Overview' },
  { id: 'work', label: 'Work' },
  { id: 'approvals', label: 'Approvals' },
  { id: 'knowledge', label: 'Knowledge' },
  { id: 'lessons', label: 'Lessons' },
  { id: 'memory', label: 'Memory' },
  { id: 'dreaming', label: 'Dreaming' }
] as const;

export type ProjectTab = (typeof PROJECT_TABS)[number]['id'];

export function isProjectTab(value: string | null | undefined): value is ProjectTab {
  return PROJECT_TABS.some(tab => tab.id === value);
}

// ---- item counts --------------------------------------------------------------------------------

/** Totals of a project's (or feature's) item_counts: finished = complete + closed, in flight = in_progress + in_review. */
export interface ItemTotals {
  total: number;
  complete: number;
  closed: number;
  finished: number;
  inFlight: number;
  /** round(finished / total × 100), 0 with no items. */
  pct: number;
}

export function itemTotals(counts: Record<string, number> | undefined): ItemTotals {
  const count = (status: string) => counts?.[status] ?? 0;
  const total = Object.values(counts ?? {}).reduce((sum, value) => sum + value, 0);
  const complete = count('complete');
  const closed = count('closed');
  const finished = complete + closed;
  return { total, complete, closed, finished, inFlight: count('in_progress') + count('in_review'), pct: total === 0 ? 0 : Math.round((finished / total) * 100) };
}

/** The share of total in percent, for a progress bar segment. */
export function share(part: number, total: number): number {
  return total === 0 ? 0 : (part / total) * 100;
}

// ---- code reviews -------------------------------------------------------------------------------

export type ReviewState = 'running' | 'completed' | 'stale' | 'failed';

/**
 * Running (queued or running), Stale (completed but behind head), Completed, or else Failed: a run that failed or was
 * interrupted (by an API restart) needs a retry. A missing stale flag is not stale.
 */
export function reviewState(run: Pick<RepositoryReviewRunResponse, 'status' | 'stale'>): ReviewState {
  const status = run.status.toLowerCase();
  if (status === 'queued' || status === 'running') return 'running';
  if (status === 'completed') return run.stale === true ? 'stale' : 'completed';
  return 'failed';
}

export const REVIEW_STATE_LABEL: Record<ReviewState, string> = { running: 'Running', completed: 'Completed', stale: 'Stale', failed: 'Failed' };
export const REVIEW_STATE_CHIP: Record<ReviewState, string> = { running: 'cg-chip-accent', completed: 'cg-chip-ok', stale: 'cg-chip-warn', failed: 'cg-chip-err' };

/** A repository's latest review needs a person's attention when it failed or is stale. */
export function reviewNeedsAttention(review: RepositoryReviewResponse | null | undefined): boolean {
  if (!review) return false;
  const state = reviewState(review.run);
  return state === 'failed' || state === 'stale';
}

const SEVERITIES = ['critical', 'high', 'medium', 'low', 'info'];

/** "2 high · 5 medium · 7 low findings", severest first; "" when there are none. */
export function findingCounts(findings: Pick<RepositoryReviewFindingResponse, 'severity'>[]): string {
  const counts = new Map<string, number>();
  for (const finding of findings) {
    const severity = (finding.severity || 'unknown').toLowerCase();
    counts.set(severity, (counts.get(severity) ?? 0) + 1);
  }
  const rank = (severity: string) => (SEVERITIES.indexOf(severity) + 1 || SEVERITIES.length + 1);
  const parts = [...counts.entries()].sort(([a], [b]) => rank(a) - rank(b)).map(([severity, count]) => `${count} ${severity}`);
  return parts.length === 0 ? '' : `${parts.join(' · ')} findings`;
}

/** One row of a project's latest code reviews: a member repository's latest run. */
export interface ReviewRow {
  repository: string;
  runId: number;
  state: ReviewState;
  label: string;
  chip: string;
  /** When it finished, or started, or was queued. */
  at: string;
  /** The finding-count line, or what stands in for it. */
  summary: string;
}

export function reviewRow(repository: string, review: RepositoryReviewResponse): ReviewRow {
  const state = reviewState(review.run);
  const counts = findingCounts(review.findings ?? []);
  const summary = state === 'running' ? 'Review in progress…' : counts || (state === 'failed' ? 'No findings — run failed' : 'No findings');
  return {
    repository,
    runId: review.run.id,
    state,
    label: REVIEW_STATE_LABEL[state],
    chip: REVIEW_STATE_CHIP[state],
    at: review.run.completedAt ?? review.run.startedAt ?? review.run.createdAt,
    summary
  };
}

// ---- projects list ------------------------------------------------------------------------------

export interface ProjectRow {
  key: string;
  name: string;
  description: string;
  repositories: string;
  pct: number;
  /** Progress bar widths in percent: finished (green) and in flight (blue). */
  done: number;
  flight: number;
  inFlight: number;
  /** Open approvals plus member repositories whose latest review failed or is stale: undefined while counting, null when it could not be counted. */
  toReview: number | null | undefined;
  updatedAt: string;
}

/** Open approvals plus the repositories whose latest code review failed or is stale. */
export function toReviewCount(openApprovals: number, latestReviews: (RepositoryReviewResponse | null | undefined)[]): number {
  return openApprovals + latestReviews.filter(reviewNeedsAttention).length;
}

export function projectRow(project: ProjectView, toReview: number | null | undefined): ProjectRow {
  const totals = itemTotals(project.item_counts);
  const repositoryCount = project.repositories.length;
  return {
    key: project.key,
    name: project.name,
    description: project.description ?? '',
    repositories: `${repositoryCount} ${repositoryCount === 1 ? 'repository' : 'repositories'}`,
    pct: totals.pct,
    done: share(totals.finished, totals.total),
    flight: share(totals.inFlight, totals.total),
    inFlight: totals.inFlight,
    toReview,
    updatedAt: project.updated_at
  };
}

// ---- in progress --------------------------------------------------------------------------------

/** Items in progress or in review, or blocked waiting on a person: those waiting on a person first, then newest first. */
export function inProgressItems(items: ItemView[]): ItemView[] {
  const awaiting = (item: ItemView) => item.blocked && item.blocked_reason === 'awaiting_human';
  return items
    .filter(item => item.status === 'in_progress' || item.status === 'in_review' || awaiting(item))
    .sort((a, b) => Number(awaiting(b)) - Number(awaiting(a)) || b.updated_at.localeCompare(a.updated_at));
}

export interface InProgressRow {
  key: string;
  title: string;
  /** "{feature key} · {holder}"; the time is added by the template. */
  meta: string;
  updatedAt: string;
  status: WorkStatus;
  awaitingHuman: boolean;
}

export function inProgressRow(item: ItemView): InProgressRow {
  return {
    key: item.key,
    title: item.title,
    meta: [item.feature?.key, item.claim?.holder ?? 'unclaimed'].filter(Boolean).join(' · '),
    updatedAt: item.updated_at,
    status: item.status,
    awaitingHuman: item.blocked && item.blocked_reason === 'awaiting_human'
  };
}

// ---- reads --------------------------------------------------------------------------------------

/** The counts of things waiting on review: open approvals of a project and member repositories' latest code reviews. */
@Injectable({ providedIn: 'root' })
export class ProjectSignalsService {
  private readonly api = inject(ApiService);
  private readonly knowledge = inject(KnowledgeApiService);

  /** The open approval items of a project: the review queue's own total, from one request for a single item. */
  async openApprovals(project: string): Promise<number> {
    const result = await firstValueFrom(this.knowledge.reviewQueue(project, 1));
    return result.total ?? result.items.length;
  }

  /** The latest code review of a repository; null when it was never reviewed (404) or cannot be read. */
  async latestReview(repository: string): Promise<RepositoryReviewResponse | null> {
    try {
      return await firstValueFrom(this.api.getLatestRepositoryReview(repository));
    } catch {
      return null;
    }
  }

  /** The latest review of each repository, read once per repository even when shared through cache. */
  latestReviews(repositories: string[], cache = new Map<string, Promise<RepositoryReviewResponse | null>>()): Promise<(RepositoryReviewResponse | null)[]> {
    return Promise.all(repositories.map(repository => {
      let pending = cache.get(repository);
      if (!pending) {
        pending = this.latestReview(repository);
        cache.set(repository, pending);
      }
      return pending;
    }));
  }
}
