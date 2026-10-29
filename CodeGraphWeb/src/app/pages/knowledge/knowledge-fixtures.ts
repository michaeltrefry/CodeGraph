// Sample rows for the knowledge page specs (plain data, no test framework imports).
import { Challenge, KnowledgeEntry, LessonRow, ReviewItem, WorkActor } from './knowledge.models';

export const HUMAN: WorkActor = { name: 'michael', kind: 'user' };
export const AGENT: WorkActor = { name: 'michael', kind: 'session', session_id: 's-1', harness: 'claude-code', model: 'opus', host: 'dev-mac' };

export function entry(overrides: Partial<KnowledgeEntry> = {}): KnowledgeEntry {
  return {
    entity: 'knowledge',
    id: 'e-1',
    project_id: 'p-1',
    project_key: 'CG',
    kind: 'decision',
    status: 'active',
    summary: 'Use SSE for the message stream',
    confidence: 'medium',
    possibly_stale: false,
    revision: 1,
    version: 3,
    author: AGENT,
    verified_at: '2026-10-28T10:00:00Z',
    created_at: '2026-10-28T10:00:00Z',
    updated_at: '2026-10-28T10:00:00Z',
    ...overrides
  };
}

export function challenge(overrides: Partial<Challenge> = {}): Challenge {
  return {
    id: 'c-1',
    entry_id: 'r-1',
    status: 'open',
    reason: 'Release branches need it',
    evidence: [{ kind: 'link', summary: 'RELEASING.md', ref: 'docs/RELEASING.md' }],
    author: AGENT,
    created_at: '2026-10-28T11:00:00Z',
    ...overrides
  };
}

export function rule(overrides: Partial<KnowledgeEntry> = {}): KnowledgeEntry {
  return entry({
    id: 'r-1',
    kind: 'lesson',
    summary: 'Never push to main',
    author: HUMAN,
    version: 5,
    details: { applies_when: { activity: ['merging'] }, source: { kind: 'prompt', priority: 'highest' } },
    lesson: {
      state: 'rule',
      priority: 'highest',
      recurrence: { events: 2, before_rule: 0, while_served: 1, while_not_served: 0, unknown: 0 },
      rule_reason: 'human_prompt',
      rule_since: '2026-10-28T10:00:00Z',
      challenges: [challenge()]
    },
    ...overrides
  });
}

export function lessonRow(overrides: Partial<LessonRow> = {}): LessonRow {
  return {
    id: 'r-1',
    summary: 'Never push to main',
    version: 5,
    state: 'rule',
    priority: 'highest',
    recurrence: { events: 3, before_rule: 0, while_served: 2, while_not_served: 1, unknown: 0 },
    created_at: '2026-10-28T10:00:00Z',
    activity: ['merging'],
    source: 'prompt',
    rule_reason: 'human_prompt',
    rule_since: '2026-10-28T10:00:00Z',
    author: HUMAN,
    ...overrides
  };
}

export function reviewItem(overrides: Partial<ReviewItem> = {}): ReviewItem {
  return {
    kind: 'proposed_rule',
    entry: { id: 'l-1', kind: 'lesson', summary: 'Squash fixups before review', confidence: 'medium', version: 2 },
    priority: 'normal',
    recurrence: 1,
    since: '2026-10-28T12:00:00Z',
    author: AGENT,
    ...overrides
  };
}
