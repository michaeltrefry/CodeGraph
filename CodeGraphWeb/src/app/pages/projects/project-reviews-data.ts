import { RepositoryReviewRunSummary } from '../../core/models';
import { REVIEW_STATE_CHIP, REVIEW_STATE_LABEL, ReviewState, reviewState } from './project-data';

/** The loaded history of one repository: its runs newest first, and where the next older page starts. */
export interface RepoRuns {
  items: RepositoryReviewRunSummary[];
  nextBefore: number | null;
  /** True when the history could not be read. */
  failed?: boolean;
}

export interface SeverityChip {
  severity: string;
  label: string;
  /** cg-chip-err for critical and high, cg-chip-warn for medium, plain otherwise. */
  chip: string;
}

/** One row of the code reviews table. */
export interface ReviewRunRow {
  key: string;
  repository: string;
  runId: number;
  state: ReviewState;
  label: string;
  chip: string;
  /** True on the newest run of its repository. */
  latest: boolean;
  severities: SeverityChip[];
  /** What stands in for the severity chips when there are none. */
  noFindings: string;
  commit: string;
  model: string;
  startedBy: string;
  /** When it finished, or started, or was queued. */
  at: string;
  action: 'Open' | 'Watch' | 'Retry';
}

const SEVERITY_ORDER = ['critical', 'high', 'medium', 'low', 'info'];

function severityChip(severity: string): string {
  return severity === 'critical' || severity === 'high' ? 'cg-chip-err' : severity === 'medium' ? 'cg-chip-warn' : '';
}

/** "2 high", "5 medium", "7 low" chips, severest first; zero counts are left out. */
export function severityChips(counts: Record<string, number> | undefined): SeverityChip[] {
  const rank = (severity: string) => (SEVERITY_ORDER.indexOf(severity) + 1 || SEVERITY_ORDER.length + 1);
  return Object.entries(counts ?? {})
    .map(([severity, count]) => [severity.toLowerCase(), count] as const)
    .filter(([, count]) => count > 0)
    .sort(([a], [b]) => rank(a) - rank(b))
    .map(([severity, count]) => ({ severity, label: `${count} ${severity}`, chip: severityChip(severity) }));
}

function noFindingsText(state: ReviewState): string {
  return state === 'running' ? 'In progress…' : state === 'failed' ? 'No findings — run failed' : 'No findings';
}

/** The instant of a timestamp; one without a zone is UTC. */
function instant(value: string): number {
  const time = Date.parse(/Z$|[+-]\d\d:?\d\d$/.test(value) ? value : `${value}Z`);
  return Number.isNaN(time) ? 0 : time;
}

/** Newest first: by when the run was requested, then by id. */
export function newestFirst(a: RepositoryReviewRunSummary, b: RepositoryReviewRunSummary): number {
  return instant(b.createdAt) - instant(a.createdAt) || b.id - a.id;
}

export function reviewRunRow(run: RepositoryReviewRunSummary, latest: boolean): ReviewRunRow {
  const state = reviewState(run);
  const severities = severityChips(run.findingCounts);
  return {
    key: `${run.repository}#${run.id}`,
    repository: run.repository,
    runId: run.id,
    state,
    label: REVIEW_STATE_LABEL[state],
    chip: REVIEW_STATE_CHIP[state],
    latest,
    severities,
    noFindings: severities.length === 0 ? noFindingsText(state) : '',
    commit: run.reviewedCommitSha ? run.reviewedCommitSha.slice(0, 7) : '—',
    model: run.model || '—',
    startedBy: run.requestedBy || '—',
    at: run.completedAt ?? run.startedAt ?? run.createdAt,
    action: state === 'running' ? 'Watch' : state === 'failed' ? 'Retry' : 'Open'
  };
}

/**
 * The runs of the given repositories merged newest first. The "latest" pill goes on the newest run of
 * each repository (of its whole loaded history, so a filter never moves it).
 */
export function mergeRuns(byRepo: Record<string, RepoRuns>, repositories: string[]): ReviewRunRow[] {
  const rows: { run: RepositoryReviewRunSummary; latest: boolean }[] = [];
  for (const repository of repositories) {
    const runs = [...(byRepo[repository]?.items ?? [])].sort(newestFirst);
    runs.forEach((run, index) => rows.push({ run, latest: index === 0 }));
  }
  return rows.sort((a, b) => newestFirst(a.run, b.run)).map(({ run, latest }) => reviewRunRow(run, latest));
}

/** A repository's runs with a freshly read first page folded in (by run id); older pages already loaded stay. */
export function foldFirstPage(existing: RepoRuns | undefined, page: RepoRuns): RepoRuns {
  if (!existing || existing.items.length === 0) return page;
  const byId = new Map(existing.items.map(run => [run.id, run]));
  for (const run of page.items) byId.set(run.id, run);
  return { items: [...byId.values()].sort(newestFirst), nextBefore: existing.nextBefore };
}
