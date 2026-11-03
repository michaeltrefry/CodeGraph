// The Work tab's features table: rows derived from FeatureView, the filters and the sort, and their
// round trip through the URL's query params. Pure functions, so the view is a projection of the state.
import { FeatureView } from '../work/work.models';
import { itemTotals, share } from './project-data';

export type FeatureStatusFilter = 'all' | 'open' | 'complete';
export type FeatureSort = 'updated' | 'created' | 'pct' | 'remaining' | 'key';
export type SortDir = 'asc' | 'desc';

export interface FeatureFilter {
  /** Matched case-insensitively against the key and the title. */
  q: string;
  status: FeatureStatusFilter;
  /** Only features with items in progress or in review. */
  inProg: boolean;
  /** Only features with uncovered requirements. */
  uncovered: boolean;
  /** Only features created by this actor; '' is anyone. */
  by: string;
  sort: FeatureSort;
  dir: SortDir;
}

export const DEFAULT_FEATURE_FILTER: Readonly<FeatureFilter> = { q: '', status: 'all', inProg: false, uncovered: false, by: '', sort: 'updated', dir: 'desc' };

export const FEATURE_SORTS: readonly { value: FeatureSort; label: string }[] = [
  { value: 'updated', label: 'Last updated' },
  { value: 'created', label: 'Created' },
  { value: 'pct', label: '% complete' },
  { value: 'remaining', label: 'Items remaining' },
  { value: 'key', label: 'Key' }
];

const STATUSES: readonly FeatureStatusFilter[] = ['all', 'open', 'complete'];
const SORTS = FEATURE_SORTS.map(sort => sort.value);

/** A feature with what the table shows and sorts by. */
export interface FeatureRow {
  feature: FeatureView;
  key: string;
  title: string;
  status: FeatureView['status'];
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  total: number;
  finished: number;
  remaining: number;
  /** round(finished / total × 100), 0 with no items. */
  pct: number;
  /** in_progress + in_review. */
  inFlight: number;
  uncovered: number;
  blocked: number;
  /** Progress bar widths in percent. */
  bar: { complete: number; closed: number; flight: number };
}

export function featureRow(feature: FeatureView): FeatureRow {
  const totals = itemTotals(feature.item_counts);
  return {
    feature,
    key: feature.key,
    title: feature.title,
    status: feature.status,
    createdBy: feature.created_by,
    createdAt: feature.created_at,
    updatedAt: feature.updated_at,
    total: totals.total,
    finished: totals.finished,
    remaining: totals.total - totals.finished,
    pct: totals.pct,
    inFlight: totals.inFlight,
    uncovered: feature.completion?.uncovered_requirements?.length ?? 0,
    blocked: feature.blocked_count ?? 0,
    bar: { complete: share(totals.complete, totals.total), closed: share(totals.closed, totals.total), flight: share(totals.inFlight, totals.total) }
  };
}

export interface FeatureTable {
  /** The rows that pass every filter, sorted. */
  rows: FeatureRow[];
  /** All features. */
  total: number;
  /** Features per status, whatever the other filters are. */
  statusCounts: Record<FeatureStatusFilter, number>;
  /** The distinct creators, in order of first appearance. */
  creators: string[];
}

const time = (value: string) => {
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? 0 : parsed;
};
const keyOrder = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });

const COMPARE: Record<FeatureSort, (a: FeatureRow, b: FeatureRow) => number> = {
  updated: (a, b) => time(a.updatedAt) - time(b.updatedAt),
  created: (a, b) => time(a.createdAt) - time(b.createdAt),
  pct: (a, b) => a.pct - b.pct,
  remaining: (a, b) => a.remaining - b.remaining,
  key: (a, b) => keyOrder(a.key, b.key)
};

/** Filters and sorts the features; ties keep the order the features came in, in either direction. */
export function featureTable(features: readonly FeatureView[], filter: FeatureFilter): FeatureTable {
  const all = features.map(featureRow);
  const q = filter.q.trim().toLowerCase();
  const open = all.filter(row => row.status === 'open').length;
  const complete = all.filter(row => row.status === 'complete').length;
  const rows = all.filter(row =>
    (!q || row.key.toLowerCase().includes(q) || row.title.toLowerCase().includes(q)) &&
    (filter.status === 'all' || row.status === filter.status) &&
    (!filter.inProg || row.inFlight > 0) &&
    (!filter.uncovered || row.uncovered > 0) &&
    (!filter.by || row.createdBy === filter.by));
  const compare = COMPARE[filter.sort];
  const sign = filter.dir === 'asc' ? 1 : -1;
  rows.sort((a, b) => sign * compare(a, b));
  return { rows, total: all.length, statusCounts: { all: all.length, open, complete }, creators: [...new Set(all.map(row => row.createdBy))] };
}

/** Whether any filter (not the sort) differs from the default. */
export function hasFilters(filter: FeatureFilter): boolean {
  return !!filter.q.trim() || filter.status !== 'all' || filter.inProg || filter.uncovered || !!filter.by;
}

/** The filters reset; the sort is kept. */
export function clearFilters(filter: FeatureFilter): FeatureFilter {
  return { ...filter, q: '', status: 'all', inProg: false, uncovered: false, by: '' };
}

/** A header click: the active column flips its direction; a new column sorts descending, the key ascending. */
export function sortByColumn(filter: FeatureFilter, column: FeatureSort): FeatureFilter {
  if (filter.sort === column) return { ...filter, dir: filter.dir === 'asc' ? 'desc' : 'asc' };
  return { ...filter, sort: column, dir: column === 'key' ? 'asc' : 'desc' };
}

/** "13 features", or "5 of 13 features" when the filters hide some. */
export function shownText(shown: number, total: number): string {
  const noun = total === 1 ? 'feature' : 'features';
  return shown === total ? `${total} ${noun}` : `${shown} of ${total} ${noun}`;
}

/** "{n} uncovered requirement(s)". */
export function uncoveredLabel(count: number): string {
  return `${count} uncovered requirement${count === 1 ? '' : 's'}`;
}

// ---- query params -------------------------------------------------------------------------------

/** The filter as query params, defaults left out (an empty object is the default view). */
export function filterToParams(filter: FeatureFilter): Record<string, string> {
  const params: Record<string, string> = {};
  const q = filter.q.trim();
  if (q) params['q'] = filter.q;
  if (filter.status !== DEFAULT_FEATURE_FILTER.status) params['status'] = filter.status;
  if (filter.inProg) params['inprog'] = '1';
  if (filter.uncovered) params['uncovered'] = '1';
  if (filter.by) params['by'] = filter.by;
  if (filter.sort !== DEFAULT_FEATURE_FILTER.sort) params['sort'] = filter.sort;
  if (filter.dir !== DEFAULT_FEATURE_FILTER.dir) params['dir'] = filter.dir;
  return params;
}

/** The filter of the query params; a missing or unknown value is the default. */
export function filterFromParams(params: { get(name: string): string | null }): FeatureFilter {
  const pick = <T extends string>(value: string | null, allowed: readonly T[], fallback: T): T =>
    allowed.includes(value as T) ? (value as T) : fallback;
  return {
    q: params.get('q') ?? '',
    status: pick(params.get('status'), STATUSES, DEFAULT_FEATURE_FILTER.status),
    inProg: params.get('inprog') === '1',
    uncovered: params.get('uncovered') === '1',
    by: params.get('by') ?? '',
    sort: pick(params.get('sort'), SORTS, DEFAULT_FEATURE_FILTER.sort),
    dir: pick(params.get('dir'), ['asc', 'desc'] as const, DEFAULT_FEATURE_FILTER.dir)
  };
}

/** The full date and time, for a title next to a relative time. */
export function fullDate(value: string | undefined): string {
  if (!value) return '';
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? value : new Date(parsed).toLocaleString();
}
