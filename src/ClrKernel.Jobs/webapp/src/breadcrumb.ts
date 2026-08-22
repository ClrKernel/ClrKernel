/**
 * Where you are, as the top bar says it.
 *
 * React-free so it can be unit-tested: the top bar renders whatever this
 * returns and holds no routing knowledge of its own.
 */

export interface Crumb {
  label: string;
  /** Omitted on the leaf — the page you are on is not a link to itself. */
  to?: string;
  /** Rendered as a Badge after the label. Only the notebook editor sets it. */
  badge?: string;
  /** Set when `label` is shortened; the full value goes in `title`. */
  full?: string;
}

/** Longest a crumb gets before the middle is elided. */
export const MAX_CRUMB = 42;

/**
 * Drop the middle, not the end: notebook paths differ in their last segment far
 * more often than their first, so a tail-truncated list is a column of
 * identical-looking rows.
 */
export function middleTruncate(value: string, max = MAX_CRUMB): string {
  if (value.length <= max) {
    return value;
  }
  const keep = max - 1;
  const head = Math.ceil(keep / 2);
  return `${value.slice(0, head)}…${value.slice(value.length - (keep - head))}`;
}

function titleCase(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

function leaf(label: string, badge?: string): Crumb {
  const short = middleTruncate(label);
  return { label: short, badge, ...(short === label ? {} : { full: label }) };
}

/**
 * `search` is the raw query string, because the notebook editor keeps its
 * subject in `?path=` rather than in the path itself.
 */
export function breadcrumbFor(pathname: string, search = ''): Crumb[] {
  const params = new URLSearchParams(search);
  const segments = pathname.split('/').filter(Boolean);

  if (segments.length === 0) {
    return [leaf('Dashboard')];
  }

  switch (segments[0]) {
    case 'jobs':
      // /jobs/:env/new and /jobs/:env/:name — the env is part of the identity,
      // so it rides along as the badge rather than becoming its own crumb.
      return segments.length >= 3
        ? [
            { label: 'Jobs', to: '/jobs' },
            leaf(segments[2] === 'new' ? 'New job' : decodeURIComponent(segments[2]), segments[1]),
          ]
        : [leaf('Jobs')];

    case 'notebooks':
      return [leaf('Notebooks')];

    case 'channels':
      return [leaf('Channels')];

    case 'settings':
      // /settings/:section — the section is a tab, and a tab you can link to is
      // a place, so it earns a crumb. Capitalised rather than looked up: the
      // titles live on the server and the breadcrumb is rendered before they
      // arrive.
      return segments.length >= 2
        ? [{ label: 'Settings', to: '/settings' }, leaf(titleCase(segments[1]))]
        : [leaf('Settings')];

    case 'runs':
      return [
        { label: 'Jobs', to: '/jobs' },
        leaf(segments[1] ? `Run ${segments[1]}` : 'Run'),
      ];

    case 'edit': {
      const path = params.get('path');
      // Editing is always dev — production is promoted to, never edited.
      return [
        { label: 'Notebooks', to: '/notebooks' },
        leaf(path ?? 'Untitled', 'dev'),
      ];
    }

    default:
      return [leaf('Not found')];
  }
}
