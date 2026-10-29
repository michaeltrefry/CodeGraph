import { describe, expect, it } from 'vitest';
import { buildTimeline, changeLines, evidenceTitle, pushCommits } from './work-format';
import { fixtureItem } from './work-fixtures';
import { timeAgo } from './work-shared';
import { MessageView, WorkChange, actorLabel } from './work.models';

const agent = { name: 'michael', kind: 'session' as const, session_id: 's1', harness: 'claude-code', model: 'opus', host: 'mac-studio' };

describe('actorLabel', () => {
  it('names a person plainly and an agent session with harness, model and host (E3)', () => {
    expect(actorLabel({ name: 'michael', kind: 'user' })).toBe('michael');
    expect(actorLabel(agent)).toBe('michael (claude-code / opus @ mac-studio)');
  });
});

describe('buildTimeline', () => {
  const all = new Map();
  const item = fixtureItem(7, { status: 'in_review' }, all);
  item.comments = [{ id: 'c', author: 'bob', body: 'Looks right', created_at: '2026-01-02T10:00:00Z' }];
  item.handoffs = [{ id: 'h', author: 'michael', session_id: 's1', harness: 'claude-code', created_at: '2026-01-02T09:00:00Z', done: 'Wired the route', next_action: 'Add tests' }];
  item.attempts = [{ id: 'a', number: 1, outcome: 'superseded', holder: { identity: 'michael', session_id: 's1', harness: 'claude-code' }, started_at: '2026-01-02T08:00:00Z', ended_at: '2026-01-02T08:30:00Z', reason: 'Plan changed' }];
  item.links = [{ id: 'l', kind: 'pr', url: 'https://github.com/o/r/pull/9', created_at: '2026-01-02T11:00:00Z' }];
  item.acceptance_report = { reported_by: 'michael', reported_at: '2026-01-02T12:00:00Z', summary: 'All met', criteria: [{ id: 'A1', state: 'met', evidence: 'ci run' }] };
  const changes: WorkChange[] = [
    { sequence: 1, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'i', entity_key: 'W-7', kind: 'item_created', actor: agent, changes: [], created_at: '2026-01-02T07:00:00Z' },
    { sequence: 2, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'i', entity_key: 'W-7', kind: 'item_updated', actor: agent, changes: [{ field: 'status', old: 'ready', new: 'in_progress' }], created_at: '2026-01-02T07:30:00Z' },
    { sequence: 3, project_id: 'p', project_key: 'W', entity: 'item', entity_id: 'o', entity_key: 'W-8', kind: 'item_updated', actor: agent, changes: [{ field: 'status', old: 'a', new: 'b' }], created_at: '2026-01-02T07:40:00Z' }
  ];
  const messages: MessageView[] = [
    { id: 'm', project_key: 'W', channel: 'item', kind: 'question', from: { name: 'michael', kind: 'user' }, body: 'Which branch?', created_at: '2026-01-02T10:30:00Z', state: 'open', item: 'W-7' }
  ];
  const timeline = buildTimeline(item, changes, messages);

  it('puts status moves, comments, handoffs, attempts, links, messages and acceptance in one newest-first list', () => {
    expect(timeline.map(entry => entry.kind)).toEqual(['acceptance', 'link', 'message', 'comment', 'handoff', 'attempt', 'attempt', 'status', 'change']);
    expect(timeline.find(entry => entry.kind === 'status')?.title).toBe('Status ready → in_progress');
  });

  it('leaves out the changes of other items and keeps who did each thing', () => {
    expect(timeline.some(entry => entry.title.includes('W-8'))).toBe(false);
    expect(timeline.find(entry => entry.kind === 'status')?.actor).toMatchObject({ harness: 'claude-code', model: 'opus', host: 'mac-studio' });
    expect(timeline.find(entry => entry.kind === 'handoff')?.actor).toMatchObject({ session_id: 's1', harness: 'claude-code' });
    expect(timeline.find(entry => entry.kind === 'link')?.url).toContain('/pull/9');
  });
});

describe('formatting', () => {
  it('shows field changes as old and new values', () => {
    const lines = changeLines({ changes: [{ field: 'status', old: 'ready', new: 'in_progress' }, { field: 'goal', new: 'x'.repeat(100) }] } as WorkChange);
    expect(lines[0]).toEqual({ field: 'status', old: 'ready', new: 'in_progress' });
    expect(lines[1].old).toBe('∅');
    expect(lines[1].new.endsWith('…')).toBe(true);
  });

  it('says how long ago, treating a server time without a zone as UTC', () => {
    const now = Date.parse('2026-01-01T01:00:00Z');
    expect(timeAgo('2026-01-01T00:59:55', now)).toBe('just now');
    expect(timeAgo('2026-01-01T00:30:00Z', now)).toBe('30m ago');
    expect(timeAgo('2026-01-01T03:00:00Z', now)).toBe('in 2h');
  });
});

describe('buildTimeline with GitHub evidence', () => {
  const item = fixtureItem(7, { status: 'in_review' }, new Map());
  const evidence = [
    { id: 'e1', kind: 'review', repository: 'codegraph', pull_request: 9, url: 'https://github.com/o/r/pull/9', author: 'github:reviewer', source: 'human', priority: 'normal', review_state: 'changes_requested', body: 'Needs a test', superseded: false, created_at: '2026-01-02T13:00:00Z' },
    { id: 'e2', kind: 'merge', repository: 'codegraph', pull_request: 9, author: 'github:octocat', source: 'human', priority: 'normal', superseded: true, created_at: '2026-01-02T14:00:00Z' }
  ];

  it('puts reviews and merges on the timeline with the GitHub author, verdict and link', () => {
    const timeline = buildTimeline(item, [], [], evidence);
    expect(timeline.map(entry => entry.kind)).toEqual(['github', 'github']);
    expect(timeline[1].title).toBe('Review #9 (codegraph): changes requested');
    expect(timeline[1].actor).toMatchObject({ name: 'github:reviewer', kind: 'system' });
    expect(timeline[1].detail).toBe('Needs a test');
    expect(timeline[1].url).toContain('/pull/9');
    expect(timeline[0].detail).toContain('superseded attempt');
  });

  it('shows a push as its commits: short SHA, message and pusher', () => {
    const push = { id: 'p1', kind: 'push', repository: 'codegraph', url: 'https://github.com/o/r/commit/1111111aaaa', author: 'claude-agent', source: 'human', priority: 'normal', superseded: false, created_at: '2026-01-02T12:00:00Z', external_id: '1111111aaaa2222222222222222222222222222', body: '0000000abcdef Add the table\n1111111aaaa2222222222222222222222222222 Record evidence' };
    expect(pushCommits(push)).toEqual([{ sha: '0000000', message: 'Add the table' }, { sha: '1111111', message: 'Record evidence' }]);
    const entry = buildTimeline(item, [], [], [push])[0];
    expect(entry.title).toBe('Pushed 2 commits (head 1111111)');
    expect(entry.detail).toBe('0000000 Add the table\n1111111 Record evidence');
    expect(entry.actor).toMatchObject({ name: 'claude-agent', kind: 'system' });
    expect(evidenceTitle({ ...evidence[1], external_id: 'abcdef0123456789' })).toBe('Pull request merged #9 (codegraph) as abcdef0');
  });

  it('names evidence kinds and marks bots', () => {
    expect(evidenceTitle({ ...evidence[0], source: 'bot', review_state: 'approved' })).toBe('Review #9 (codegraph): approved [bot]');
    expect(evidenceTitle({ ...evidence[1], kind: 'pr_closed' })).toBe('Pull request closed without merging #9 (codegraph)');
  });
});
