// The wire shapes of the work tools (docs/design/agent-coordination.md; src/CodeGraph.Models/Work).
// Names stay snake_case, as the REST routes send and read them, so nothing is translated twice.

export type WorkStatus = 'backlog' | 'ready' | 'in_progress' | 'in_review' | 'complete' | 'closed';
export type WorkPriority = 'low' | 'medium' | 'high' | 'critical';
export type WorkItemType = 'story' | 'task' | 'bug';
export type BlockedReason = 'waiting' | 'cancelled_prerequisite' | 'awaiting_human';
export type MessageKind = 'info' | 'question' | 'request' | 'blocker' | 'announcement';

export const WORK_STATUSES: readonly WorkStatus[] = ['backlog', 'ready', 'in_progress', 'in_review', 'complete', 'closed'];
export const WORK_PRIORITIES: readonly WorkPriority[] = ['low', 'medium', 'high', 'critical'];
export const WORK_ITEM_TYPES: readonly WorkItemType[] = ['story', 'task', 'bug'];
export const MESSAGE_KINDS: readonly MessageKind[] = ['info', 'question', 'request', 'blocker', 'announcement'];

export interface WorkActor {
  name: string;
  kind: 'user' | 'session' | 'system';
  session_id?: string;
  harness?: string;
  model?: string;
  host?: string;
}

export interface Holder {
  identity: string;
  session_id?: string;
  harness?: string;
  model?: string;
  host?: string;
}

export interface RepositoryMember { repository: string; role: 'owned' | 'dependency' }

export interface ProjectView {
  kind: 'project';
  id: string;
  key: string;
  name: string;
  description?: string;
  goals: string[];
  repositories: RepositoryMember[];
  version: number;
  created_by: string;
  created_at: string;
  updated_at: string;
  item_counts?: Record<string, number>;
}

export interface Requirement { id: string; text: string; state: 'active' | 'dropped'; decision?: string; covered_by?: string[] }

export interface FeatureView {
  kind: 'feature';
  id: string;
  key: string;
  project_id: string;
  project_key: string;
  title: string;
  description?: string;
  documents: { id: string; title: string; body: string; position: number; created_at?: string; updated_at?: string }[];
  version: number;
  created_by: string;
  created_at: string;
  updated_at: string;
  item_counts?: Record<string, number>;
  status: 'open' | 'complete';
  completion?: { items: number; finished: number; complete: number; uncovered_requirements?: string[] };
  goals?: string[];
  requirements?: Requirement[];
  acceptance_tests?: string[];
}

export interface DependencyRef { id: string; key: string; title: string; status: WorkStatus }
export interface Claim { holder: string; marker: string; claimed_at: string; expires_at: string; session_id?: string }
export interface WorkLink { id: string; kind: 'pr' | 'branch' | 'other'; url: string; created_at: string }
export interface WorkComment { id: string; author: string; body: string; created_at: string; session_id?: string; harness?: string }
export interface Criterion { id: string; text: string; state: 'unmet' | 'met'; evidence?: string }

export interface Handoff {
  id: string;
  author: string;
  created_at: string;
  session_id?: string;
  harness?: string;
  done?: string;
  decisions?: string[];
  open_issues?: string[];
  corrections?: string[];
  next_action?: string;
  branch?: string;
  commit?: string;
}

export interface Attempt {
  id: string;
  number: number;
  outcome: 'active' | 'superseded' | 'abandoned' | 'complete' | 'closed';
  holder: Holder;
  started_at: string;
  ended_at?: string;
  reason?: string;
  branch?: string;
  branches?: string[];
  prs?: string[];
}

export interface AcceptanceReport {
  reported_by: string;
  reported_at: string;
  summary?: string;
  criteria?: { id: string; state: 'unmet' | 'met'; evidence?: string }[];
  session_id?: string;
}

export interface MessageLine {
  id: string;
  kind: MessageKind;
  from: string;
  summary: string;
  created_at: string;
  from_session_id?: string;
  item?: string;
  state?: 'open' | 'answered' | 'declined';
  blocking?: boolean;
}

/** A knowledge entry as recall answers it: a row, or with detail full the whole entry with its author (E3). */
export interface KnowledgeEntry extends KnowledgeRow {
  author?: WorkActor;
  body?: string;
  revision?: number;
  created_at?: string;
  updated_at?: string;
  details?: { why?: string; why_failed?: string; what?: string };
  revisions?: { revision?: number; author?: WorkActor; created_at?: string; summary?: string }[];
}

export interface KnowledgeImportCandidate {
  index: number;
  outcome: string;
  message?: string;
  duplicate_of?: KnowledgeRow;
  entry?: KnowledgeRow;
}

export interface KnowledgeImportResult { stored: boolean; preview_token: string; candidates: KnowledgeImportCandidate[] }

export interface KnowledgeRow {
  id: string;
  kind: string;
  summary: string;
  confidence?: string;
  version?: number;
  possibly_stale?: boolean;
  status?: string;
  made_autonomously?: boolean;
  item?: string;
}

export interface ItemView {
  kind: 'item';
  id: string;
  key: string;
  project_id: string;
  project_key: string;
  feature?: { id: string; key: string; title: string };
  type: WorkItemType;
  title: string;
  description?: string;
  status: WorkStatus;
  priority: WorkPriority;
  order: number;
  labels: string[];
  repository?: string;
  blocked: boolean;
  blocked_reason?: BlockedReason;
  blocked_by: DependencyRef[];
  blocks: DependencyRef[];
  claim?: Claim;
  links: WorkLink[];
  comments: WorkComment[];
  version: number;
  created_by: string;
  created_at: string;
  updated_at: string;
  handoffs?: Handoff[];
  handoff_count?: number;
  ref?: string;
  goal?: string;
  acceptance_criteria?: Criterion[];
  inherits?: string[];
  inherited_requirements?: Requirement[];
  touches?: string[];
  merged_awaiting_acceptance?: boolean;
  acceptance_report?: AcceptanceReport;
  attempts?: Attempt[];
  attempt_count?: number;
  open_messages?: MessageLine[];
  knowledge?: KnowledgeRow[];
  /** Rules matched to this item (S9), read only here. */
  rules?: { id: string; summary: string; version: number; priority: string; activity: string[]; recurrence?: number }[];
}

export interface WorkChange {
  sequence: number;
  project_id: string;
  project_key: string;
  entity: 'project' | 'feature' | 'item';
  entity_id: string;
  entity_key: string;
  kind: string;
  actor: WorkActor;
  changes: { field: string; old?: unknown; new?: unknown; old_truncated?: boolean; new_truncated?: boolean }[];
  created_at: string;
}

export interface ChangesResult { items: WorkChange[]; next_cursor: string }

export interface SessionView {
  id: string;
  project_key: string;
  identity: string;
  harness: string;
  model?: string;
  host?: string;
  repository?: string;
  branch?: string;
  worktree?: string;
  intent?: string;
  live: boolean;
  started_at: string;
  last_seen_at: string;
  ended_at?: string;
}

export interface HoldView {
  id: string;
  resource: string;
  project: string;
  holder: Holder;
  purpose: string;
  state: 'waiting' | 'held' | 'released' | 'expired' | 'cancelled';
  priority: WorkPriority;
  ttl_seconds: number;
  requested_at: string;
  item?: string;
  granted_at?: string;
  expires_at?: string;
  queue_position?: number;
  owner_unreachable?: boolean;
  owner_unreachable_since?: string;
}

export interface ResourceView {
  id: string;
  project: string;
  name: string;
  capacity: number;
  labels: string[];
  used: number;
  available: boolean;
  holders: HoldView[];
  waiters?: HoldView[];
}

export interface MessageView {
  id: string;
  project_key: string;
  channel: string;
  kind: MessageKind;
  from: WorkActor;
  body: string;
  created_at: string;
  state?: 'open' | 'answered' | 'declined';
  blocking?: boolean;
  to_session_id?: string;
  to_human?: boolean;
  item?: string;
  feature?: string;
  resource?: string;
  in_reply_to?: string;
  resolved_by?: WorkActor;
  resolved_at?: string;
  delivery?: string;
}

export interface MessageEvent {
  sequence: number;
  project_key: string;
  message_id: string;
  kind: string;
  actor: WorkActor;
  created_at: string;
  recipient?: string;
  message?: MessageLine;
}

export interface FindResult<T> { items: T[]; next_cursor?: string }

export interface PlanChange { ref: string; key?: string; title: string; changes?: unknown; pivot?: boolean; reopens_finished?: boolean }

export interface PlanPreview {
  kind: 'plan_preview';
  preview_token: string;
  expires_at: string;
  feature: string;
  feature_version: number;
  unchanged: boolean;
  requirements_added?: string[];
  requirements_changed?: string[];
  requirements_dropped?: string[];
  items_added?: PlanChange[];
  items_edited?: PlanChange[];
  items_closed?: PlanChange[];
  dependencies_added?: { item: string; blocked_by: string }[];
  dependencies_removed?: { item: string; blocked_by: string }[];
  pivots?: { key: string; action: string; holder?: Holder }[];
  warnings?: string[];
}

export interface WorkErrorBody { code: string; message: string; current?: unknown; reason?: string }

export class WorkApiError extends Error {
  constructor(readonly status: number, readonly code: string, message: string, readonly current?: unknown) {
    super(message);
  }

  get isConflict(): boolean {
    return this.code === 'conflict' || this.code === 'already_claimed' || this.code === 'claim_lost';
  }
}

/** The actor of a write as a short label: a person, or "harness / model @ host" for a session. */
export function actorLabel(actor: Pick<WorkActor, 'name' | 'kind' | 'harness' | 'model' | 'host'>): string {
  if (actor.kind !== 'session') return actor.name;
  const detail = [actor.harness, actor.model].filter(Boolean).join(' / ');
  const where = actor.host ? ` @ ${actor.host}` : '';
  return `${actor.name} (${detail}${where})`;
}

/** GitHub evidence of an item (S6): pushes, pull requests, reviews and comments, authored by a GitHub login. */
export interface GitHubEvidence {
  id: string;
  kind: string;
  repository?: string;
  pull_request?: number;
  url?: string;
  author: string;
  source: 'human' | 'bot' | string;
  priority: string;
  review_state?: string;
  location?: string;
  body?: string;
  attempt_id?: string;
  superseded: boolean;
  /** The head SHA of a push, the merge commit of a merge, the id of a review or comment. */
  external_id?: string;
  created_at: string;
}
