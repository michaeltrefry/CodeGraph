import { convertToParamMap } from '@angular/router';
import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FEATURE_FILTER, FeatureFilter, FeatureSort, clearFilters, featureRow, featureTable, filterFromParams, filterToParams, hasFilters,
  shownText, sortByColumn, uncoveredLabel
} from './feature-filter';
import { feature, featureSet } from './project-fixtures';

const keys = (filter: Partial<FeatureFilter>) => featureTable(featureSet(), { ...DEFAULT_FEATURE_FILTER, ...filter }).rows.map(row => row.key);

describe('featureRow', () => {
  it('derives total, finished, remaining, pct, in flight, uncovered and blocked', () => {
    const [search, billing, login, empty] = featureSet().map(featureRow);
    expect(search).toMatchObject({ total: 4, finished: 1, remaining: 3, pct: 25, inFlight: 0, uncovered: 2, blocked: 2 });
    expect(billing).toMatchObject({ total: 4, finished: 4, remaining: 0, pct: 100, uncovered: 0, blocked: 0 });
    expect(login).toMatchObject({ total: 4, finished: 1, remaining: 3, pct: 25, inFlight: 2, uncovered: 1 });
    expect(login.bar).toEqual({ complete: 25, closed: 0, flight: 50 });
    // No items: 0%, nothing remaining; a missing blocked_count is 0.
    expect(empty).toMatchObject({ total: 0, finished: 0, remaining: 0, pct: 0, inFlight: 0, uncovered: 0, blocked: 0 });
  });

  it('rounds the percentage', () => {
    expect(featureRow(feature('X', { item_counts: { complete: 2, ready: 1 } })).pct).toBe(67);
  });
});

describe('featureTable filters', () => {
  it('sorts by last updated, newest first, by default', () => {
    expect(keys({})).toEqual(['W-F2', 'W-F1', 'W-F3', 'W-F10', 'W-F4']);
  });

  it('matches the search on key or title, case-insensitively', () => {
    expect(keys({ q: 'SEARCH' })).toEqual(['W-F2', 'W-F4']);
    expect(keys({ q: 'w-f1' })).toEqual(['W-F1', 'W-F10']);
    expect(keys({ q: '  login ' })).toEqual(['W-F1']);
    expect(keys({ q: 'nothing like it' })).toEqual([]);
  });

  it('filters by status', () => {
    expect(keys({ status: 'complete' })).toEqual(['W-F10']);
    expect(keys({ status: 'open' })).toEqual(['W-F2', 'W-F1', 'W-F3', 'W-F4']);
  });

  it('keeps features with items in progress or in review', () => {
    expect(keys({ inProg: true })).toEqual(['W-F1', 'W-F4']);
  });

  it('keeps features with uncovered requirements', () => {
    expect(keys({ uncovered: true })).toEqual(['W-F2', 'W-F1']);
  });

  it('filters by creator', () => {
    expect(keys({ by: 'alice' })).toEqual(['W-F2', 'W-F4']);
    expect(keys({ by: 'nobody' })).toEqual([]);
  });

  it('combines the filters', () => {
    expect(keys({ q: 'search', by: 'alice', inProg: true })).toEqual(['W-F4']);
    expect(keys({ status: 'open', uncovered: true, inProg: true })).toEqual(['W-F1']);
    expect(keys({ status: 'complete', inProg: true })).toEqual([]);
  });

  it('counts each status over all features, whatever the other filters', () => {
    const table = featureTable(featureSet(), { ...DEFAULT_FEATURE_FILTER, q: 'search', status: 'complete', inProg: true, by: 'alice' });
    expect(table.rows).toEqual([]);
    expect(table.total).toBe(5);
    expect(table.statusCounts).toEqual({ all: 5, open: 4, complete: 1 });
    expect(table.creators).toEqual(['alice', 'michael', 'bob']);
  });
});

describe('featureTable sort', () => {
  it.each<[FeatureSort, string[]]>([
    ['updated', ['W-F4', 'W-F10', 'W-F3', 'W-F1', 'W-F2']],
    ['created', ['W-F1', 'W-F2', 'W-F3', 'W-F10', 'W-F4']],
    ['key', ['W-F1', 'W-F2', 'W-F3', 'W-F4', 'W-F10']]
  ])('sorts by %s ascending and descending', (sort, ascending) => {
    expect(keys({ sort, dir: 'asc' })).toEqual(ascending);
    expect(keys({ sort, dir: 'desc' })).toEqual([...ascending].reverse());
  });

  it('sorts by % complete, ties in the order the features came in', () => {
    // pct: F2 25, F10 100, F1 25, F3 0, F4 0.
    expect(keys({ sort: 'pct', dir: 'asc' })).toEqual(['W-F3', 'W-F4', 'W-F2', 'W-F1', 'W-F10']);
    expect(keys({ sort: 'pct', dir: 'desc' })).toEqual(['W-F10', 'W-F2', 'W-F1', 'W-F3', 'W-F4']);
  });

  it('sorts by items remaining, ties in the order the features came in', () => {
    // remaining: F2 3, F10 0, F1 3, F3 0, F4 4.
    expect(keys({ sort: 'remaining', dir: 'asc' })).toEqual(['W-F10', 'W-F3', 'W-F2', 'W-F1', 'W-F4']);
    expect(keys({ sort: 'remaining', dir: 'desc' })).toEqual(['W-F4', 'W-F2', 'W-F1', 'W-F10', 'W-F3']);
  });

  it('sorts the filtered rows', () => {
    expect(keys({ by: 'alice', sort: 'created', dir: 'asc' })).toEqual(['W-F2', 'W-F4']);
  });
});

describe('header clicks, clearing and labels', () => {
  it('flips the active column; a new column sorts descending, the key ascending', () => {
    const start = { ...DEFAULT_FEATURE_FILTER };
    expect(sortByColumn(start, 'updated')).toMatchObject({ sort: 'updated', dir: 'asc' });
    expect(sortByColumn({ ...start, dir: 'asc' }, 'updated')).toMatchObject({ sort: 'updated', dir: 'desc' });
    expect(sortByColumn({ ...start, dir: 'asc' }, 'pct')).toMatchObject({ sort: 'pct', dir: 'desc' });
    expect(sortByColumn(start, 'key')).toMatchObject({ sort: 'key', dir: 'asc' });
    expect(sortByColumn({ ...start, sort: 'key', dir: 'asc' }, 'key')).toMatchObject({ sort: 'key', dir: 'desc' });
  });

  it('knows when a filter is active and clears the filters but not the sort', () => {
    expect(hasFilters(DEFAULT_FEATURE_FILTER)).toBe(false);
    expect(hasFilters({ ...DEFAULT_FEATURE_FILTER, sort: 'key', dir: 'asc' })).toBe(false);
    expect(hasFilters({ ...DEFAULT_FEATURE_FILTER, q: '   ' })).toBe(false);
    for (const patch of [{ q: 'x' }, { status: 'open' as const }, { inProg: true }, { uncovered: true }, { by: 'alice' }]) {
      expect(hasFilters({ ...DEFAULT_FEATURE_FILTER, ...patch })).toBe(true);
    }
    const busy: FeatureFilter = { q: 'x', status: 'complete', inProg: true, uncovered: true, by: 'bob', sort: 'pct', dir: 'asc' };
    expect(clearFilters(busy)).toEqual({ ...DEFAULT_FEATURE_FILTER, sort: 'pct', dir: 'asc' });
  });

  it('says how many features are shown', () => {
    expect(shownText(13, 13)).toBe('13 features');
    expect(shownText(5, 13)).toBe('5 of 13 features');
    expect(shownText(1, 1)).toBe('1 feature');
    expect(uncoveredLabel(1)).toBe('1 uncovered requirement');
    expect(uncoveredLabel(3)).toBe('3 uncovered requirements');
  });
});

describe('query params', () => {
  it('leaves the defaults out of the URL', () => {
    expect(filterToParams(DEFAULT_FEATURE_FILTER)).toEqual({});
    expect(filterFromParams(convertToParamMap({}))).toEqual(DEFAULT_FEATURE_FILTER);
  });

  it('round-trips every filter and the sort', () => {
    const filter: FeatureFilter = { q: 'Search box', status: 'open', inProg: true, uncovered: true, by: 'alice', sort: 'remaining', dir: 'asc' };
    const params = filterToParams(filter);
    expect(params).toEqual({ q: 'Search box', status: 'open', inprog: '1', uncovered: '1', by: 'alice', sort: 'remaining', dir: 'asc' });
    expect(filterFromParams(convertToParamMap(params))).toEqual(filter);

    for (const sort of ['updated', 'created', 'pct', 'remaining', 'key'] as FeatureSort[]) {
      for (const dir of ['asc', 'desc'] as const) {
        const round = filterFromParams(convertToParamMap(filterToParams({ ...DEFAULT_FEATURE_FILTER, sort, dir })));
        expect(round).toEqual({ ...DEFAULT_FEATURE_FILTER, sort, dir });
      }
    }
  });

  it('reads an unknown value as the default', () => {
    expect(filterFromParams(convertToParamMap({ status: 'weird', sort: 'size', dir: 'up', inprog: 'yes' })))
      .toEqual(DEFAULT_FEATURE_FILTER);
  });
});
