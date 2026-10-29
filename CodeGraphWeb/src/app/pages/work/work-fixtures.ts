import { BlockedReason, DependencyRef, ItemView, WorkStatus } from './work.models';

interface Spec {
  status?: WorkStatus;
  blockedBy?: number[];
  awaitingHuman?: boolean;
  claim?: string;
}

/** An item as the server answers it: blocked and blocked_reason derived from its blockers, as the store derives them. */
export function fixtureItem(number: number, spec: Spec, all: Map<number, ItemView>): ItemView {
  const status = spec.status ?? 'backlog';
  const blockedBy: DependencyRef[] = (spec.blockedBy ?? []).map(blocker => {
    const view = all.get(blocker)!;
    return { id: view.id, key: view.key, title: view.title, status: view.status };
  });
  const unfinished = blockedBy.filter(blocker => blocker.status !== 'complete' && blocker.status !== 'closed');
  const closed = blockedBy.some(blocker => blocker.status === 'closed');
  const finished = status === 'complete' || status === 'closed';
  let reason: BlockedReason | undefined;
  if (!finished) {
    if (closed || unfinished.length > 0) reason = closed ? 'cancelled_prerequisite' : 'waiting';
    else if (spec.awaitingHuman) reason = 'awaiting_human';
  }
  return {
    kind: 'item',
    id: `00000000-0000-0000-0000-${String(number).padStart(12, '0')}`,
    key: `W-${number}`,
    project_id: 'p',
    project_key: 'W',
    feature: { id: 'f', key: 'W-F1', title: 'Fixture feature' },
    type: 'story',
    title: `Item ${number}`,
    status,
    priority: 'medium',
    order: number,
    labels: [],
    blocked: reason !== undefined,
    blocked_reason: reason,
    blocked_by: blockedBy,
    blocks: [],
    claim: spec.claim ? { holder: spec.claim, marker: 'ui', claimed_at: '2026-01-01T00:00:00Z', expires_at: '2026-01-01T01:00:00Z' } : undefined,
    links: [],
    comments: [],
    version: 1,
    created_by: 'michael',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z'
  };
}

/**
 * A 50-item feature shaped to exercise every case of the graph:
 *  W-1 complete root; W-2..W-9 eight items that all unblock when it is done (parallel set);
 *  W-10..W-17 follow them one to one; W-18..W-29 a chain of twelve behind W-10 (the critical path);
 *  W-30 closed; W-31 behind it (cancelled prerequisite); W-32 behind W-31 (stuck downstream);
 *  W-33 awaiting a person; W-34..W-50 independent items in assorted states.
 */
export function fiftyItemFeature(): ItemView[] {
  const all = new Map<number, ItemView>();
  const add = (number: number, spec: Spec = {}) => all.set(number, fixtureItem(number, spec, all));

  add(1, { status: 'complete' });
  for (let n = 2; n <= 9; n++) add(n, { status: n === 2 ? 'in_progress' : 'ready', blockedBy: [1], claim: n === 2 ? 'claude-code' : undefined });
  for (let n = 10; n <= 17; n++) add(n, { blockedBy: [n - 8] });
  add(18, { blockedBy: [10] });
  for (let n = 19; n <= 29; n++) add(n, { blockedBy: [n - 1] });
  add(30, { status: 'closed' });
  add(31, { blockedBy: [30] });
  add(32, { blockedBy: [31] });
  add(33, { status: 'ready', awaitingHuman: true });
  for (let n = 34; n <= 50; n++) add(n, { status: n % 5 === 0 ? 'complete' : n % 3 === 0 ? 'in_review' : 'ready' });

  const items = [...all.values()];
  // blocks is the inverse of blocked_by.
  for (const item of items) {
    item.blocks = items.filter(other => other.blocked_by.some(blocker => blocker.key === item.key)).map(other => ({ id: other.id, key: other.key, title: other.title, status: other.status }));
  }
  return items;
}
