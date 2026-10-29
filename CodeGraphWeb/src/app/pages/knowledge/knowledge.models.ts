// Wire shapes of the S5/S9 knowledge tools over REST (snake_case, as WorkJson writes them).
// Optional members are left out by the server when they say nothing.

export interface WorkActor {
  name: string;
  kind: string;
  session_id?: string;
  harness?: string;
  model?: string;
  host?: string;
}

export interface WorkError {
  code: string;
  message: string;
  current?: unknown;
  reason?: string;
}

export type KnowledgeKind = 'goal' | 'decision' | 'constraint' | 'fact' | 'gotcha' | 'attempt' | 'architecture' | 'lesson';
export type KnowledgeStatus = 'active' | 'superseded' | 'retracted';
export type KnowledgeConfidence = 'high' | 'medium' | 'low';
export type LessonActivity =
  | 'planning' | 'slicing' | 'implementing' | 'testing' | 'reviewing' | 'merging' | 'releasing' | 'tracker_updates' | 'coordinating';
export type CorrectionSourceKind = 'prompt' | 'review' | 'bot_review' | 'subagent' | 'agent';
export type LessonRuleState = 'candidate' | 'proposed' | 'rule' | 'rejected';
export type RuleReason = 'human_prompt' | 'recurrence' | 'approved';
export type AddressedByKind = 'skill' | 'instruction' | 'hook' | 'test' | 'ci';
export type ChallengeStatus = 'open' | 'upheld' | 'amended' | 'retracted';
export type ChallengeOutcome = 'uphold' | 'amend' | 'retract';
export type EvidenceKind = 'test_result' | 'source' | 'counter_example' | 'link' | 'other';
export type LessonsView = 'recently_learned' | 'recurrence' | 'unaddressed' | 'unmatched' | 'unlearned_reviews';
export type ReviewKind = 'challenge' | 'proposed_rule' | 'contradiction' | 'autonomous_decision';

export const KNOWLEDGE_KINDS: KnowledgeKind[] = ['goal', 'decision', 'constraint', 'fact', 'gotcha', 'attempt', 'architecture', 'lesson'];
export const LESSON_ACTIVITIES: LessonActivity[] = [
  'planning', 'slicing', 'implementing', 'testing', 'reviewing', 'merging', 'releasing', 'tracker_updates', 'coordinating'
];
export const CORRECTION_SOURCES: CorrectionSourceKind[] = ['prompt', 'review', 'bot_review', 'subagent', 'agent'];
export const RULE_STATES: LessonRuleState[] = ['candidate', 'proposed', 'rule', 'rejected'];
export const ADDRESSED_BY_KINDS: AddressedByKind[] = ['skill', 'instruction', 'hook', 'test', 'ci'];
export const EVIDENCE_KINDS: EvidenceKind[] = ['test_result', 'source', 'counter_example', 'link', 'other'];

export interface ProjectRow {
  id: string;
  key: string;
  kind: string;
  title: string;
}

export interface FindWorkResult<T> {
  items: T[];
  next_cursor?: string;
}

export interface AppliesWhen {
  activity?: LessonActivity[];
  repositories?: string[];
  paths?: string[];
  tools?: string[];
  harness?: string;
}

export interface KnowledgeDetails {
  why?: string;
  alternatives?: string[];
  context?: string;
  consequences?: string;
  why_failed?: string;
  what?: string;
  applies_when?: AppliesWhen;
  source?: { kind: CorrectionSourceKind; priority: string; ref?: string };
}

export interface KnowledgeRevision {
  revision: number;
  action: string;
  author: WorkActor;
  created_at: string;
}

export interface KnowledgeRow {
  id: string;
  kind: KnowledgeKind;
  summary: string;
  confidence: KnowledgeConfidence;
  version: number;
  status?: KnowledgeStatus;
  possibly_stale?: boolean;
  made_autonomously?: boolean;
  item?: string;
  rule?: boolean;
  recurrence?: number;
}

export interface Evidence {
  kind: EvidenceKind;
  summary: string;
  ref?: string;
}

export interface Challenge {
  id: string;
  entry_id: string;
  status: ChallengeStatus;
  reason: string;
  evidence: Evidence[];
  author: WorkActor;
  created_at: string;
  resolution_reason?: string;
  resolved_by?: WorkActor;
  resolved_at?: string;
}

export interface AddressedBy {
  kind: AddressedByKind;
  name: string;
  ref?: string;
  author: WorkActor;
  created_at: string;
  harness?: string;
}

export interface Recurrence {
  events: number;
  before_rule: number;
  while_served: number;
  while_not_served: number;
  unknown: number;
}

export interface LessonLink {
  id: string;
  summary: string;
  status: KnowledgeStatus;
  state?: LessonRuleState;
}

export interface LessonEvent {
  correction_id: string;
  counted: boolean;
  summary: string;
  author: WorkActor;
  created_at: string;
  source?: CorrectionSourceKind;
  source_ref?: string;
  what?: string;
  rule_at_event?: boolean;
  served?: boolean;
  session_id?: string;
}

export interface LessonState {
  state: LessonRuleState;
  priority: string;
  recurrence: Recurrence;
  rule_reason?: RuleReason;
  rule_since?: string;
  rejected_reason?: string;
  contradicts?: LessonLink[];
  challenges?: Challenge[];
  addressed_by?: AddressedBy[];
  served_sessions?: number;
  last_served_at?: string;
  events?: LessonEvent[];
}

export interface EntityRef {
  id: string;
  key: string;
  title: string;
}

export interface KnowledgeEntry {
  entity: 'knowledge';
  id: string;
  project_id: string;
  project_key: string;
  kind: KnowledgeKind;
  status: KnowledgeStatus;
  summary: string;
  confidence: KnowledgeConfidence;
  possibly_stale: boolean;
  revision: number;
  version: number;
  author: WorkActor;
  verified_at: string;
  created_at: string;
  updated_at: string;
  body?: string;
  details?: KnowledgeDetails;
  made_autonomously?: boolean;
  repositories?: string[];
  paths?: string[];
  nodes?: { id: number; repository: string; qualified_name: string; file_path?: string }[];
  item?: EntityRef;
  feature?: EntityRef;
  supersedes?: string;
  superseded_by?: string;
  status_reason?: string;
  verified_commit?: string;
  verified_files?: { repository: string; path: string }[];
  stale_files?: { repository: string; path: string }[];
  source_file?: string;
  revisions?: KnowledgeRevision[];
  lesson?: LessonState;
}

export interface RecallResult<T> {
  items: T[];
  next_cursor?: string;
}

export interface RecordResult {
  created: boolean;
  entry: KnowledgeEntry;
  superseded?: KnowledgeRow;
  merged?: boolean;
  hints?: string[];
}

export interface PromoteResult {
  entry: KnowledgeEntry;
  changed: boolean;
}

export interface ChallengeResult {
  created: boolean;
  challenge: Challenge;
  rule: KnowledgeEntry;
}

export interface LessonRow {
  id: string;
  summary: string;
  version: number;
  state: LessonRuleState;
  priority: string;
  recurrence: Recurrence;
  created_at: string;
  activity?: LessonActivity[];
  source?: CorrectionSourceKind;
  rule_reason?: RuleReason;
  rule_since?: string;
  last_served_at?: string;
  contradicted?: boolean;
  open_challenges?: number;
  addressed_by?: AddressedBy[];
  author?: WorkActor;
}

export interface LessonGroup {
  activity: LessonActivity;
  lessons: LessonRow[];
}

export interface UnlearnedReview {
  evidence_id: string;
  item: string;
  kind: string;
  author_login: string;
  source: string;
  priority: string;
  created_at: string;
  url?: string;
  external_id?: string;
  review_state?: string;
  body?: string;
}

export interface LessonsResult {
  view: LessonsView;
  items?: LessonRow[];
  groups?: LessonGroup[];
  next_cursor?: string;
  reviews?: UnlearnedReview[];
}

export interface ReviewItem {
  kind: ReviewKind;
  entry: KnowledgeRow;
  priority: string;
  recurrence: number;
  since: string;
  other?: KnowledgeRow;
  challenges?: Challenge[];
  more_challenges?: number;
  author?: WorkActor;
  other_author?: WorkActor;
}

export interface ReviewQueueResult {
  items: ReviewItem[];
  next_cursor?: string;
}

// ---- request bodies -----------------------------------------------------------------------

export interface RecallFilters {
  project: string;
  kind?: KnowledgeKind[];
  repository?: string;
  paths?: string[];
  activity?: LessonActivity;
  source?: CorrectionSourceKind;
  rule_state?: LessonRuleState[];
  text?: string;
  status?: KnowledgeStatus[];
  possibly_stale?: boolean;
  detail?: 'summary' | 'full';
  limit?: number;
  cursor?: string;
}

export interface LessonsFilters {
  project: string;
  view: LessonsView;
  activity?: LessonActivity;
  source?: CorrectionSourceKind;
  since_days?: number;
  limit?: number;
  cursor?: string;
}

export interface RevisionBody {
  id: string;
  expected_version: number;
  summary?: string;
  body?: string;
  confidence?: KnowledgeConfidence;
  applies_when?: AppliesWhen;
}

export interface RecordBody {
  project?: string;
  idempotency_key?: string;
  entry?: {
    kind: KnowledgeKind;
    summary: string;
    body?: string;
    confidence?: KnowledgeConfidence;
    why_failed?: string;
    supersedes?: { id: string; expected_version: number };
  };
  revise?: RevisionBody;
  retract?: { id: string; expected_version: number; reason: string };
  verify?: { id: string; expected_version: number };
  relate?: { id: string; expected_version: number; same_as?: string; contradicts?: string };
  acknowledge?: { id: string; expected_version: number };
}

export interface PromoteBody {
  expected_version: number;
  action: 'propose' | 'approve' | 'reject' | 'addressed_by';
  reason?: string;
  addressed_by?: { kind: AddressedByKind; name: string; ref?: string; harness?: string };
}

export interface ChallengeBody {
  idempotency_key?: string;
  open?: { id: string; reason: string; evidence: Evidence[] };
  resolve?: {
    challenge_id: string;
    expected_version: number;
    outcome: ChallengeOutcome;
    reason: string;
    amend?: { summary?: string; what?: string; applies_when?: AppliesWhen };
  };
}
