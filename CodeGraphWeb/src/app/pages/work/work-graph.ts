import { BlockedReason, ItemView, WorkStatus } from './work.models';

/** One item of a feature as the dependency graph sees it. */
export interface GraphNode {
  key: string;
  title: string;
  status: WorkStatus;
  blocked: boolean;
  blockedReason?: BlockedReason;
  /** A blocker, direct or further up, is closed: nothing will free this item until the plan changes. */
  stuck: boolean;
  /** A direct blocker is closed (blocked_reason cancelled_prerequisite). */
  cancelledPrerequisite: boolean;
  awaitingHuman: boolean;
  finished: boolean;
  /** Layer on screen: the longest chain of blockers above the item, finished or not. */
  layer: number;
  /** 0 for unfinished items with nothing unfinished in front of them; one more per unfinished blocker generation. */
  wave: number;
  /** Can be started now: unfinished, every blocker complete, not waiting on a person. */
  ready: boolean;
  onCriticalPath: boolean;
  claimedBy?: string;
  x: number;
  y: number;
}

export interface GraphEdge { from: string; to: string; critical: boolean; cancelled: boolean }

export interface ParallelSet { wave: number; keys: string[] }

export interface WorkGraph {
  nodes: GraphNode[];
  edges: GraphEdge[];
  /** Keys of the longest chain of unfinished, not stuck items, first to last. */
  criticalPath: string[];
  /** Unfinished, not stuck items grouped by wave: the items of one wave can run side by side. */
  parallelSets: ParallelSet[];
  layers: number;
  width: number;
  height: number;
}

export const NODE_WIDTH = 176;
export const NODE_HEIGHT = 40;
const COLUMN_GAP = 64;
const ROW_GAP = 14;
const MARGIN = 24;

const isFinished = (status: WorkStatus): boolean => status === 'complete' || status === 'closed';

/**
 * Builds the dependency graph of a feature's items. blocked_by is the source of truth; blockers
 * outside the given items (another feature's) are ignored, with their edge. A cycle is refused by
 * the server, but if one appears the nodes of it are laid out at layer 0 rather than looping.
 */
export function buildWorkGraph(items: readonly ItemView[]): WorkGraph {
  const byKey = new Map(items.map(item => [item.key, item]));
  const blockersOf = (item: ItemView): ItemView[] =>
    item.blocked_by.map(dependency => byKey.get(dependency.key)).filter((blocker): blocker is ItemView => blocker !== undefined);

  const memo = <T>(compute: (item: ItemView, recurse: (other: ItemView) => T) => T, fallback: T) => {
    const done = new Map<string, T>();
    const visiting = new Set<string>();
    const visit = (item: ItemView): T => {
      if (done.has(item.key)) return done.get(item.key)!;
      if (visiting.has(item.key)) return fallback;
      visiting.add(item.key);
      const value = compute(item, visit);
      visiting.delete(item.key);
      done.set(item.key, value);
      return value;
    };
    return visit;
  };

  const layerOf = memo<number>((item, recurse) => blockersOf(item).reduce((deepest, blocker) => Math.max(deepest, recurse(blocker) + 1), 0), 0);
  const waveOf = memo<number>((item, recurse) => {
    const unfinished = blockersOf(item).filter(blocker => !isFinished(blocker.status));
    return unfinished.length === 0 ? 0 : Math.max(...unfinished.map(recurse)) + 1;
  }, 0);
  const stuckOf = memo<boolean>(
    (item, recurse) => blockersOf(item).some(blocker => blocker.status === 'closed' || (!isFinished(blocker.status) && recurse(blocker))),
    false
  );
  // The longest chain of unfinished, not stuck items ending at an item (its length and predecessor).
  const chainOf = memo<{ length: number; previous?: string }>((item, recurse) => {
    let best: { length: number; previous?: string } = { length: 1 };
    for (const blocker of blockersOf(item)) {
      if (isFinished(blocker.status) || stuckOf(blocker)) continue;
      const candidate = recurse(blocker).length + 1;
      if (candidate > best.length || (candidate === best.length && best.previous !== undefined && blocker.key < best.previous)) {
        best = { length: candidate, previous: blocker.key };
      }
    }
    return best;
  }, { length: 1 });

  const candidates = items.filter(item => !isFinished(item.status) && !stuckOf(item));
  let tail: ItemView | undefined;
  for (const item of candidates) {
    const length = chainOf(item).length;
    if (!tail || length > chainOf(tail).length || (length === chainOf(tail).length && item.key < tail.key)) tail = item;
  }
  const criticalPath: string[] = [];
  // A cycle (refused by the server) must not loop here: each key is visited once.
  for (let key = tail?.key; key !== undefined && !criticalPath.includes(key); key = chainOf(byKey.get(key)!).previous) criticalPath.unshift(key);
  const critical = new Set(criticalPath);

  const nodes: GraphNode[] = items.map(item => {
    const unfinishedBlockers = blockersOf(item).filter(blocker => !isFinished(blocker.status));
    const stuck = stuckOf(item) && !isFinished(item.status);
    return {
      key: item.key,
      title: item.title,
      status: item.status,
      blocked: item.blocked,
      blockedReason: item.blocked_reason,
      stuck,
      cancelledPrerequisite: item.blocked_reason === 'cancelled_prerequisite',
      awaitingHuman: item.blocked_reason === 'awaiting_human',
      finished: isFinished(item.status),
      layer: layerOf(item),
      wave: waveOf(item),
      ready: !isFinished(item.status) && !item.blocked && unfinishedBlockers.length === 0,
      onCriticalPath: critical.has(item.key),
      claimedBy: item.claim?.holder,
      x: 0,
      y: 0
    };
  });

  // Rows within a layer by key order, so the picture is stable between refreshes.
  const layers = nodes.reduce((count, node) => Math.max(count, node.layer + 1), 0);
  const rows = new Array<number>(layers).fill(0);
  for (const node of [...nodes].sort((a, b) => a.layer - b.layer || compareKeys(a.key, b.key))) {
    node.x = MARGIN + node.layer * (NODE_WIDTH + COLUMN_GAP);
    node.y = MARGIN + rows[node.layer]++ * (NODE_HEIGHT + ROW_GAP);
  }

  const edges: GraphEdge[] = [];
  for (const item of items) {
    for (const dependency of item.blocked_by) {
      if (!byKey.has(dependency.key)) continue;
      edges.push({
        from: dependency.key,
        to: item.key,
        critical: critical.has(dependency.key) && critical.has(item.key) && chainOf(item).previous === dependency.key,
        cancelled: dependency.status === 'closed'
      });
    }
  }

  const waves = new Map<number, string[]>();
  for (const node of nodes) {
    if (node.finished || node.stuck) continue;
    waves.set(node.wave, [...(waves.get(node.wave) ?? []), node.key]);
  }
  const parallelSets = [...waves.entries()]
    .sort(([a], [b]) => a - b)
    .map(([wave, keys]) => ({ wave, keys: keys.sort(compareKeys) }));

  return {
    nodes,
    edges,
    criticalPath,
    parallelSets,
    layers,
    width: MARGIN * 2 + layers * NODE_WIDTH + Math.max(0, layers - 1) * COLUMN_GAP,
    height: MARGIN * 2 + Math.max(1, ...rows) * NODE_HEIGHT + Math.max(0, Math.max(1, ...rows) - 1) * ROW_GAP
  };
}

/** SW-2 before SW-10: keys compared by their number. */
export function compareKeys(a: string, b: string): number {
  const number = (key: string) => Number(key.slice(key.lastIndexOf('-') + 1).replace(/\D/g, '')) || 0;
  return number(a) - number(b) || a.localeCompare(b);
}

export interface FeatureProgress {
  total: number;
  complete: number;
  closed: number;
  inFlight: number;
  blocked: { key: string; title: string; reason: BlockedReason; detail: string }[];
  /** Share of the items finished (complete or closed), 0 to 100. */
  percent: number;
}

/** Done / total, and what is blocked and why (progress view). */
export function featureProgress(items: readonly ItemView[]): FeatureProgress {
  const complete = items.filter(item => item.status === 'complete').length;
  const closed = items.filter(item => item.status === 'closed').length;
  const blocked = items
    .filter(item => item.blocked && !isFinished(item.status) && item.blocked_reason)
    .map(item => ({
      key: item.key,
      title: item.title,
      reason: item.blocked_reason!,
      detail:
        item.blocked_reason === 'awaiting_human'
          ? 'Waiting for an answer to a question for a person.'
          : item.blocked_reason === 'cancelled_prerequisite'
            ? `Prerequisite closed: ${item.blocked_by.filter(blocker => blocker.status === 'closed').map(blocker => blocker.key).join(', ')}.`
            : `Waiting for ${item.blocked_by.filter(blocker => !isFinished(blocker.status)).map(blocker => blocker.key).join(', ')}.`
    }));
  return {
    total: items.length,
    complete,
    closed,
    inFlight: items.filter(item => item.status === 'in_progress' || item.status === 'in_review').length,
    blocked,
    percent: items.length === 0 ? 0 : Math.round(((complete + closed) / items.length) * 100)
  };
}
