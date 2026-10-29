import { describe, expect, it } from 'vitest';
import { buildWorkGraph, compareKeys, featureProgress } from './work-graph';
import { fiftyItemFeature, fixtureItem } from './work-fixtures';

describe('buildWorkGraph for a 50-item feature', () => {
  const items = fiftyItemFeature();
  const graph = buildWorkGraph(items);
  const node = (key: string) => graph.nodes.find(candidate => candidate.key === key)!;

  it('has every item and an edge per dependency', () => {
    expect(items).toHaveLength(50);
    expect(graph.nodes).toHaveLength(50);
    expect(graph.edges).toHaveLength(items.reduce((sum, item) => sum + item.blocked_by.length, 0));
  });

  it('finds the critical path: the longest chain of unfinished items that can still finish', () => {
    expect(graph.criticalPath).toEqual(['W-2', 'W-10', 'W-18', ...Array.from({ length: 11 }, (_, i) => `W-${19 + i}`)]);
    expect(graph.nodes.filter(candidate => candidate.onCriticalPath)).toHaveLength(14);
    expect(graph.edges.filter(edge => edge.critical)).toHaveLength(13);
  });

  it('flags waiting, awaiting-a-person and cancelled-prerequisite items, and what is stuck behind them', () => {
    expect(node('W-10').blocked).toBe(true);
    expect(node('W-10').blockedReason).toBe('waiting');
    expect(node('W-33').awaitingHuman).toBe(true);
    expect(node('W-31').cancelledPrerequisite).toBe(true);
    expect(node('W-31').stuck).toBe(true);
    // W-32 only waits for W-31, but W-31 can never finish, so it is stuck too.
    expect(node('W-32').cancelledPrerequisite).toBe(false);
    expect(node('W-32').stuck).toBe(true);
    expect(node('W-18').stuck).toBe(false);
    expect(graph.edges.find(edge => edge.from === 'W-30' && edge.to === 'W-31')?.cancelled).toBe(true);
    expect(graph.nodes.filter(candidate => !candidate.finished && candidate.blocked)).toHaveLength(23);
  });

  it('groups what can run side by side into waves and leaves stuck items out', () => {
    const [now, ...later] = graph.parallelSets;
    expect(now.wave).toBe(0);
    expect(now.keys).toEqual(expect.arrayContaining(['W-2', 'W-3', 'W-9', 'W-33']));
    expect(now.keys).not.toContain('W-1');
    expect(graph.parallelSets[1].keys).toEqual(Array.from({ length: 8 }, (_, i) => `W-${10 + i}`));
    expect(later.at(-1)?.keys).toEqual(['W-29']);
    expect(graph.parallelSets.flatMap(set => set.keys)).not.toContain('W-31');
    expect(graph.parallelSets.flatMap(set => set.keys)).not.toContain('W-32');
  });

  it('marks ready items: unfinished, unblocked, not waiting on a person', () => {
    expect(node('W-3').ready).toBe(true);
    expect(node('W-33').ready).toBe(false);
    expect(node('W-10').ready).toBe(false);
    expect(node('W-1').ready).toBe(false);
  });

  it('lays out layers left to right without overlap', () => {
    for (const edge of graph.edges) expect(node(edge.from).x).toBeLessThan(node(edge.to).x);
    // The twenty items with nothing in front of them wrap into two columns of at most twelve.
    expect(Math.max(...graph.nodes.map(candidate => candidate.y))).toBeLessThanOrEqual(24 + 11 * 54);
    const cells = new Set(graph.nodes.map(candidate => `${candidate.x}:${candidate.y}`));
    expect(cells.size).toBe(50);
    expect(graph.layers).toBe(15);
    expect(graph.width).toBeGreaterThan(0);
  });

  it('summarises progress with the reason each item is blocked', () => {
    const progress = featureProgress(items);
    expect(progress.total).toBe(50);
    expect(progress.complete).toBe(1 + 4);
    expect(progress.closed).toBe(1);
    expect(progress.blocked.find(entry => entry.key === 'W-31')?.detail).toContain('closed: W-30');
    expect(progress.blocked.find(entry => entry.key === 'W-33')?.reason).toBe('awaiting_human');
    expect(progress.blocked.find(entry => entry.key === 'W-10')?.detail).toBe('Waiting for W-2.');
  });
});

describe('buildWorkGraph edge cases', () => {
  it('handles no items', () => {
    const graph = buildWorkGraph([]);
    expect(graph.nodes).toEqual([]);
    expect(graph.criticalPath).toEqual([]);
  });

  it('ignores blockers outside the feature and survives a cycle', () => {
    const all = new Map();
    all.set(1, fixtureItem(1, {}, all));
    all.set(2, fixtureItem(2, { blockedBy: [1] }, all));
    const a = all.get(1);
    const b = all.get(2);
    a.blocked_by = [{ id: b.id, key: b.key, title: b.title, status: b.status }, { id: 'x', key: 'OTHER-9', title: 'elsewhere', status: 'ready' }];
    const graph = buildWorkGraph([a, b]);
    expect(graph.nodes).toHaveLength(2);
    expect(graph.edges).toHaveLength(2);
  });

  it('orders keys by number', () => {
    expect(['W-10', 'W-2', 'W-1'].sort(compareKeys)).toEqual(['W-1', 'W-2', 'W-10']);
  });
});
