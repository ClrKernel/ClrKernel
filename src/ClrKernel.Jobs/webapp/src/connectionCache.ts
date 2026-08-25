/**
 * What the object tree has already asked the database, for as long as the tab is
 * open.
 *
 * Module scope rather than component state, deliberately. Every level of that tree
 * is a round trip to a real server, and holding it in the component meant that
 * glancing at Jobs and coming back re-queried a database that had not changed —
 * which is slow on a small schema and rude on a large one.
 *
 * Not localStorage either: a schema is somebody else's live state, and a cache
 * that outlived the tab would be showing tables that had since been dropped with
 * no reason for anybody to suspect it. A reload is the reset, which is the
 * behaviour people already expect of a database tool, and Refresh is the explicit
 * one for when a reload is too big a hammer.
 */

import type { ApiMetadataNode, ApiObjectDetail } from './api';

export interface TreeCache {
  /** Node key → the nodes below it. Present-but-empty means "asked, nothing there". */
  children: Record<string, ApiMetadataNode[]>;
  /** Object key → its columns, keys and indexes. */
  details: Record<string, ApiObjectDetail>;
  /** Which node keys are expanded, so the tree looks how you left it. */
  open: string[];
}

const empty: TreeCache = { children: {}, details: {}, open: [] };

let cache: TreeCache = empty;

export function readCache(): TreeCache {
  return cache;
}

export function writeCache(next: TreeCache): void {
  cache = next;
}

/** Forgets a node and everything under it — what Refresh and Disconnect mean. */
export function dropSubtree(key: string): void {
  cache = {
    children: without(cache.children, key),
    details: without(cache.details, key),
    open: cache.open.filter((k) => k !== key && !k.startsWith(key + '/')),
  };
}

/**
 * Every key that is the given one, or below it, gone.
 *
 * The separator is part of the test, not decoration: a bare `startsWith` makes
 * refreshing the schema `Sales` throw away `SalesArchive` as well, because one
 * name is a prefix of the other. Keys are built as paths precisely so that a
 * subtree can be named, and this is the half that honours it.
 */
export function without<T>(entries: Record<string, T>, key: string): Record<string, T> {
  const kept: Record<string, T> = {};
  for (const [k, value] of Object.entries(entries)) {
    if (k !== key && !k.startsWith(key + '/')) {
      kept[k] = value;
    }
  }
  return kept;
}
