import { pathFromSplat, viewOf } from './routes';

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
 * The trail for a path. Everything it needs is in the path — including the
 * project, which is why the switcher at the root of the trail can navigate at
 * all rather than quietly changing what the page you are on is about.
 */
export function breadcrumbFor(pathname: string): Crumb[] {
  const segments = pathname.split('/').filter(Boolean);

  if (segments.length === 0) {
    return [leaf('Dashboard')];
  }

  switch (segments[0]) {
    case 'jobs': {
      // /jobs/:project is the list; /jobs/:project/:env/:name is one job, and the
      // env is part of its identity so it rides along as the badge. The project
      // is the switcher at the root of the trail and is not repeated here.
      if (segments.length < 2) {
        return [leaf('Jobs')];
      }
      const to = `/jobs/${segments[1]}`;
      return segments.length >= 4
        ? [
            { label: 'Jobs', to },
            leaf(segments[3] === 'new' ? 'New job' : decodeURIComponent(segments[3]), segments[2]),
          ]
        : [leaf('Jobs')];
    }

    case 'files': {
      if (segments.length < 2) {
        return [leaf('Files')];
      }
      const to = `/files/${segments[1]}`;
      // /files/:project/<view>/:branch/*path. The badge is a switcher here rather
      // than a label: which branch you are reading is a place you can move to.
      // This only says where it goes; the top bar renders it.
      if (viewOf(pathname) != null) {
        return [
          { label: 'Files', to },
          leaf(pathFromSplat(segments.slice(4).join('/')) || 'Untitled', 'branch'),
        ];
      }
      return [leaf('Files')];
    }

    case 'connections':
      // /connections/:id — which connection you have open is a place, so it earns
      // a crumb. The name is not in the path (the id is), so the page fills it in
      // by rendering its own heading; the crumb says only that you are inside one.
      return segments.length >= 2
        ? [{ label: 'Connections', to: '/connections' }, leaf('Query')]
        : [leaf('Connections')];

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
      // A run belongs to a project, but the crumb only has the path to go on —
      // so Jobs here means "the jobs you were last looking at", which /jobs
      // resolves for itself.
      return [
        { label: 'Jobs', to: '/jobs' },
        leaf(segments[1] ? `Run ${segments[1]}` : 'Run'),
      ];

    default:
      return [leaf('Not found')];
  }
}
