import { GitHubEvidence, ItemView, MessageView, WorkActor, WorkChange } from './work.models';

function show(value: unknown): string {
  if (value === undefined || value === null) return '∅';
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  return text.length > 70 ? `${text.slice(0, 69)}…` : text;
}

export interface ChangeLine { field: string; old: string; new: string }

/** The field changes of a feed entry as short old → new pairs. */
export function changeLines(change: WorkChange, max = 4): ChangeLine[] {
  return (change.changes ?? []).slice(0, max).map(entry => ({ field: entry.field, old: show(entry.old), new: show(entry.new) }));
}

const KIND_LABELS: Record<string, string> = {
  project_created: 'created the project',
  project_updated: 'updated the project',
  feature_created: 'created the feature',
  feature_updated: 'updated the feature',
  item_created: 'created',
  item_updated: 'updated',
  claimed: 'claimed',
  recovered: 'recovered'
};

export function changeVerb(change: WorkChange): string {
  return KIND_LABELS[change.kind] ?? change.kind.replace(/_/g, ' ');
}

export type TimelineKind = 'github' | 'status' | 'change' | 'comment' | 'handoff' | 'message' | 'attempt' | 'link' | 'acceptance';

export interface TimelineEntry {
  at: string;
  kind: TimelineKind;
  /** Who did it: a person, or a session with harness, model and host (E3). */
  actor?: Partial<WorkActor> & { name: string };
  title: string;
  detail?: string;
  /** The link a PR or branch entry points to. */
  url?: string;
  sort: number;
}

const toMillis = (value: string): number => Date.parse(/Z$|[+-]\d\d:?\d\d$/.test(value) ? value : `${value}Z`) || 0;

/**
 * Everything that happened to an item, newest first: status moves and other writes from the change
 * feed, comments, handoffs, execution attempts, links (PRs and branches), messages on its thread and
 * the acceptance report. Review evidence from GitHub (S6) joins here when its read route exists.
 */
export function buildTimeline(item: ItemView, changes: readonly WorkChange[], messages: readonly MessageView[], evidence: readonly GitHubEvidence[] = []): TimelineEntry[] {
  const entries: TimelineEntry[] = [];
  const add = (entry: Omit<TimelineEntry, 'sort'>) => entries.push({ ...entry, sort: toMillis(entry.at) });
  const person = (name: string, sessionId?: string, harness?: string) => ({ name, kind: (sessionId ? 'session' : 'user') as WorkActor['kind'], session_id: sessionId, harness });

  for (const change of changes) {
    if (change.entity_key !== item.key) continue;
    const moved = (change.changes ?? []).find(entry => entry.field === 'status');
    if (change.kind.endsWith('_created')) {
      add({ at: change.created_at, kind: 'change', actor: change.actor, title: 'Created' });
    } else if (moved) {
      add({ at: change.created_at, kind: 'status', actor: change.actor, title: `Status ${show(moved.old)} → ${show(moved.new)}` });
    } else {
      const fields = (change.changes ?? []).map(entry => entry.field).join(', ');
      add({ at: change.created_at, kind: 'change', actor: change.actor, title: `${changeVerb(change)}${fields ? `: ${fields}` : ''}` });
    }
  }

  for (const comment of item.comments) {
    add({ at: comment.created_at, kind: 'comment', actor: person(comment.author, comment.session_id, comment.harness), title: 'Comment', detail: comment.body });
  }

  for (const handoff of item.handoffs ?? []) {
    const parts = [
      handoff.done && `Done: ${handoff.done}`,
      handoff.next_action && `Next: ${handoff.next_action}`,
      handoff.open_issues?.length && `Open: ${handoff.open_issues.join('; ')}`,
      handoff.branch && `Branch: ${handoff.branch}`,
      handoff.commit && `Commit: ${handoff.commit}`
    ].filter(Boolean);
    add({ at: handoff.created_at, kind: 'handoff', actor: person(handoff.author, handoff.session_id, handoff.harness), title: 'Handoff', detail: parts.join('\n') });
  }

  for (const attempt of item.attempts ?? []) {
    const holder = { name: attempt.holder.identity, kind: (attempt.holder.session_id ? 'session' : 'user') as WorkActor['kind'], session_id: attempt.holder.session_id, harness: attempt.holder.harness, model: attempt.holder.model, host: attempt.holder.host };
    add({ at: attempt.started_at, kind: 'attempt', actor: holder, title: `Attempt ${attempt.number} started`, detail: attempt.branch ? `Branch ${attempt.branch}` : undefined });
    if (attempt.ended_at) {
      add({ at: attempt.ended_at, kind: 'attempt', actor: holder, title: `Attempt ${attempt.number} ${attempt.outcome}`, detail: [attempt.reason, ...(attempt.prs ?? [])].filter(Boolean).join('\n') || undefined });
    }
  }

  for (const link of item.links) {
    add({ at: link.created_at, kind: 'link', title: link.kind === 'pr' ? 'Pull request linked' : link.kind === 'branch' ? 'Branch linked' : 'Link added', detail: link.url, url: link.url });
  }

  for (const message of messages) {
    add({ at: message.created_at, kind: 'message', actor: message.from, title: `${message.kind}${message.state ? ` (${message.state})` : ''}`, detail: message.body });
  }

  for (const entry of evidence) {
    if (entry.kind === 'push') {
      const commits = pushCommits(entry);
      add({
        at: entry.created_at,
        kind: 'github',
        actor: { name: entry.author, kind: 'system' },
        title: `Pushed ${commits.length || 1} commit${commits.length === 1 || commits.length === 0 ? '' : 's'}${entry.external_id ? ` (head ${shortSha(entry.external_id)})` : ''}`,
        detail: commits.map(commit => `${commit.sha} ${commit.message}`.trimEnd()).join('\n') || undefined,
        url: entry.url
      });
      continue;
    }
    add({
      at: entry.created_at,
      kind: 'github',
      actor: { name: entry.author, kind: 'system' },
      title: evidenceTitle(entry),
      detail: [entry.body, entry.location && `at ${entry.location}`, entry.superseded && 'From a superseded attempt: moved nothing.'].filter(Boolean).join('\n') || undefined,
      url: entry.url
    });
  }

  if (item.acceptance_report) {
    const report = item.acceptance_report;
    add({
      at: report.reported_at,
      kind: 'acceptance',
      actor: person(report.reported_by, report.session_id),
      title: 'Acceptance reported',
      detail: [report.summary, ...(report.criteria ?? []).map(criterion => `${criterion.id}: ${criterion.state}${criterion.evidence ? ` (${criterion.evidence})` : ''}`)].filter(Boolean).join('\n')
    });
  }

  return entries.sort((a, b) => b.sort - a.sort);
}

export const shortSha = (sha: string): string => sha.slice(0, 7);

export interface PushCommit { sha: string; message: string }

/** The commits of a push row: its body holds one "sha message" per line, newest last. */
export function pushCommits(entry: GitHubEvidence): PushCommit[] {
  return (entry.body ?? '')
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean)
    .map(line => {
      const space = line.indexOf(' ');
      return { sha: shortSha(space < 0 ? line : line.slice(0, space)), message: space < 0 ? '' : line.slice(space + 1).trim() };
    });
}

const EVIDENCE_LABELS: Record<string, string> = {
  push: 'Pushed commits',
  pr_open: 'Pull request opened',
  pr_opened: 'Pull request opened',
  merge: 'Pull request merged',
  pr_merged: 'Pull request merged',
  pr_closed: 'Pull request closed without merging',
  review: 'Review',
  review_comment: 'Review comment',
  issue_comment: 'Comment on the pull request'
};

/** One line for a GitHub evidence entry: what happened, on which pull request, and a review's verdict. */
export function evidenceTitle(entry: GitHubEvidence): string {
  const label = EVIDENCE_LABELS[entry.kind] ?? entry.kind.replace(/_/g, ' ');
  const pull = entry.pull_request ? ` #${entry.pull_request}` : '';
  const repository = entry.repository ? ` (${entry.repository})` : '';
  const verdict = entry.review_state ? `: ${entry.review_state.replace(/_/g, ' ')}` : '';
  const bot = entry.source === 'bot' ? ' [bot]' : '';
  const sha = entry.kind === 'merge' && entry.external_id ? ` as ${shortSha(entry.external_id)}` : '';
  return `${label}${pull}${repository}${sha}${verdict}${bot}`;
}
