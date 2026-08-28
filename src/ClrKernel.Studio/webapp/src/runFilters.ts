/**
 * The monitoring grid's query, as data.
 *
 * Every one of these is applied by the server — run history grows without bound,
 * and a page the client filters after the fact gets shorter as history gets
 * longer. Keeping the filter state in one plain object, and turning it into a
 * query string in one function, is what makes that checkable without a browser.
 *
 * It lives in the URL rather than in component state so a filtered view is a
 * link: "here is the failing prod run I mean" is a thing people paste to each
 * other, and it survives the reload that follows every deploy.
 */
import type { RunStatus, RunTrigger } from './api';

/** What the server will sort on. Anything else is a 400, deliberately. */
export type RunSort = 'started' | 'created' | 'project' | 'jobName' | 'environment' | 'status' | 'trigger';

export const SORTS: RunSort[] = [
  'started', 'created', 'project', 'jobName', 'environment', 'status', 'trigger',
];

export interface RunFilters {
  project?: string;
  env?: string;
  status?: RunStatus;
  trigger?: RunTrigger;
  job?: string;
  path?: string;
  actor?: string;
  /** Local calendar dates from `<input type="date">`; converted on the way out. */
  from?: string;
  to?: string;
  sort: RunSort;
  asc: boolean;
  page: number;
}

export const PAGE_SIZE = 50;

export const NO_FILTERS: RunFilters = { sort: 'started', asc: false, page: 0 };

/**
 * A calendar day means the whole day in the reader's own timezone — the "to"
 * bound runs to midnight *after* the day they picked, because a range that
 * excluded the day you named would be a range nobody expects.
 *
 * The server compares in UTC, so this is where the conversion happens: an ISO
 * instant, not a date string, or "today" means a different eight hours depending
 * on where you are.
 */
export function dayStart(date: string): string | null {
  const parsed = new Date(`${date}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

export function dayAfter(date: string): string | null {
  const parsed = new Date(`${date}T00:00:00`);
  if (Number.isNaN(parsed.getTime())) {
    return null;
  }
  parsed.setDate(parsed.getDate() + 1);
  return parsed.toISOString();
}

/** The query string for `GET /api/runs`. One more row than a page, to answer "is there another". */
export function runsQuery(filters: RunFilters, pageSize = PAGE_SIZE): string {
  const params = new URLSearchParams();
  const put = (key: string, value: string | undefined | null) => {
    if (value) {
      params.set(key, value);
    }
  };
  put('project', filters.project);
  put('env', filters.env);
  put('status', filters.status);
  put('trigger', filters.trigger);
  put('job', filters.job);
  put('path', filters.path);
  put('actor', filters.actor);
  put('since', filters.from ? dayStart(filters.from) : undefined);
  put('until', filters.to ? dayAfter(filters.to) : undefined);
  params.set('sort', filters.sort);
  if (filters.asc) {
    params.set('asc', 'true');
  }
  params.set('limit', String(pageSize));
  if (filters.page > 0) {
    params.set('offset', String(filters.page * pageSize));
  }
  return params.toString();
}

/**
 * Clicking a column heading sorts by it, descending; clicking the one you are
 * already on flips the direction. Either way you go back to page one — staying
 * on page 4 of a differently-ordered list shows rows that were never on the page
 * you were reading.
 */
export function sortBy(filters: RunFilters, column: RunSort): RunFilters {
  return filters.sort === column
    ? { ...filters, asc: !filters.asc, page: 0 }
    : { ...filters, sort: column, asc: false, page: 0 };
}

/** Setting or clearing any filter also returns to page one, for the same reason. */
export function withFilter<K extends keyof RunFilters>(
  filters: RunFilters,
  key: K,
  value: RunFilters[K] | undefined,
): RunFilters {
  const next = { ...filters, page: 0 };
  if (value === undefined || value === '') {
    delete next[key];
  } else {
    next[key] = value;
  }
  return next;
}

/** The filters that are actually narrowing something, for the chip row. */
export function activeFilters(filters: RunFilters): { key: keyof RunFilters; value: string }[] {
  const keys: (keyof RunFilters)[] = [
    'project', 'env', 'status', 'trigger', 'job', 'path', 'actor', 'from', 'to',
  ];
  return keys
    .filter((key) => filters[key])
    .map((key) => ({ key, value: String(filters[key]) }));
}

/** Round-trips the filters through the address bar, so a filtered grid is a link. */
export function toSearch(filters: RunFilters): string {
  const params = new URLSearchParams();
  for (const { key, value } of activeFilters(filters)) {
    params.set(key, value);
  }
  if (filters.sort !== NO_FILTERS.sort) {
    params.set('sort', filters.sort);
  }
  if (filters.asc) {
    params.set('asc', '1');
  }
  if (filters.page > 0) {
    params.set('page', String(filters.page));
  }
  const query = params.toString();
  return query ? `?${query}` : '';
}

export function fromSearch(search: string): RunFilters {
  const params = new URLSearchParams(search);
  const sort = params.get('sort');
  const page = Number(params.get('page'));
  const filters: RunFilters = {
    ...NO_FILTERS,
    sort: SORTS.includes(sort as RunSort) ? (sort as RunSort) : NO_FILTERS.sort,
    asc: params.get('asc') === '1',
    page: Number.isFinite(page) && page > 0 ? Math.floor(page) : 0,
  };
  for (const key of ['project', 'env', 'status', 'trigger', 'job', 'path', 'actor', 'from', 'to'] as const) {
    const value = params.get(key);
    if (value) {
      (filters as unknown as Record<string, string>)[key] = value;
    }
  }
  return filters;
}
