import { filesPath, pathFromSplat, viewOf } from './routes';

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
  /** Set when `label` is shortened; the full value goes in `title`. */
  full?: string;
  /**
   * Rendered as a switcher rather than as text — a place in the trail you can
   * move sideways from. `label` is then only what a key and a test read.
   *
   * Both used to sit outside the trail: the project was pinned in front of it by
   * the top bar and the branch hung off the file name as a pill, so the strip
   * read "Studio / project / Files / file [branch]" — two of the four scopes out
   * of order, and the branch attached to the file rather than standing between
   * the project and it. In the path they are `/files/:project/:branch/…`, and now
   * the trail says the same thing in the same order.
   */
  slot?: 'project' | 'branch';
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

function leaf(label: string): Crumb {
  const short = middleTruncate(label);
  return { label: short, ...(short === label ? {} : { full: label }) };
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
    case 'files': {
      const [, project, branch] = segments;
      if (project == null) {
        return [leaf('Files')];
      }
      // Widest scope first, narrowing left to right: the section, the project,
      // the branch, the file. Which is the order the URL puts them in.
      const view = viewOf(pathname);
      const crumbs: Crumb[] = [
        // Back to the branch, not to the project's door: the door would bounce
        // you to whichever branch you were last on, which is not necessarily the
        // one whose file you are looking at. Not a link when it *is* that page —
        // a breadcrumb's last stop is where you are, and on the shell that is
        // Files itself, everything after it being a control rather than a place.
        view == null ? leaf('Files') : { label: 'Files', to: filesPath(project, branch) },
        { label: project, slot: 'project' },
      ];
      // The branch switcher belongs to a file, not to the shell: the shell has
      // the explorer's own branch picker two inches below it, and a second one
      // saying the same thing is a second thing to keep in agreement.
      if (view != null) {
        crumbs.push({ label: branch, slot: 'branch' });
        crumbs.push(leaf(pathFromSplat(segments.slice(4).join('/')) || 'Untitled'));
      }
      return crumbs;
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

    case 'monitoring':
    case 'notifications':
      // Views of the Dashboard rather than sections of their own, so the trail
      // says so: the tabs on the page are what move between them.
      return [{ label: 'Dashboard', to: '/' }, leaf(titleCase(segments[0]))];

    case 'runs':
      // A run belongs to a project, but the crumb only has the path to go on, so
      // it goes back to the grid that lists every run rather than guessing which
      // project's anything.
      return [
        { label: 'Monitoring', to: '/monitoring' },
        leaf(segments[1] ? `Run ${segments[1]}` : 'Run'),
      ];

    default:
      return [leaf('Not found')];
  }
}
