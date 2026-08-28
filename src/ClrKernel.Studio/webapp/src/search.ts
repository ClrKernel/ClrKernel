/**
 * The header search box, as a pure function.
 *
 * The query lives in the URL as `?q=`, not in a context: the router is already
 * there, a filtered list stays shareable and survives a reload, and the filter
 * itself is a function of (query, rows) that can be tested without a DOM.
 */

/**
 * The box is hidden where it would do nothing. A search field that silently
 * ignores what you type is worse than no search field.
 *
 * The dashboard and one project's job list — matched by shape rather than by a
 * literal: the dashboard is the whole server, and the monitoring grid does its
 * own filtering on the server rather than over what is already on screen.
 */
export function showsSearch(pathname: string): boolean {
  const segments = pathname.split('/').filter(Boolean);
  return segments.length === 0;
}

/**
 * Matches when every whitespace-separated term appears in at least one field —
 * so "daily prod" finds a prod job called "daily-load" without the two terms
 * having to be adjacent or in order.
 */
export function matchesQuery(query: string, ...fields: (string | null | undefined)[]): boolean {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) {
    return true;
  }
  const haystack = fields.filter(Boolean).join(' ').toLowerCase();
  return terms.every((term) => haystack.includes(term));
}

/** Replaces `q` while leaving every other parameter alone — `?path=` included. */
export function withQuery(search: string, query: string): string {
  const params = new URLSearchParams(search);
  if (query) {
    params.set('q', query);
  } else {
    params.delete('q');
  }
  const next = params.toString();
  return next ? `?${next}` : '';
}
