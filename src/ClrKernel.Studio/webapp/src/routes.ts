/**
 * Every URL the app builds, in one place and free of React so it can be tested.
 *
 * The shape is the same twice over: `/<section>/<project>/…`. The project is in
 * the path rather than in a context because a link has to mean one thing —
 * two projects may each have a `nightly` and each have a `reports/monthly.nb.md`
 * — and having it there is what lets the switcher in the breadcrumb actually go
 * somewhere instead of quietly changing what the page you are on is about.
 */

/**
 * How a file is being looked at. Readings of one file, so they are separate URLs
 * — the same rule the Settings tabs follow, and it is what makes a view something
 * you can link to, reload into, and go back from.
 *
 * `edit` is the notebook editor and `overview` is the jobs form; each belongs to
 * one kind of file, and the toolbar offers whichever fits. `source` and `diff`
 * are the two every file has.
 *
 * Not to be confused with read-only, which is not a view: that comes from the
 * branch, and every one of these is read-only on a branch that is not yours.
 */
export const NOTEBOOK_VIEWS = ['edit', 'overview', 'source', 'diff'] as const;
export type NotebookView = (typeof NOTEBOOK_VIEWS)[number];

/** The sections whose URL names a project. Everything else is server-wide. */
export const PROJECT_SECTIONS = ['jobs', 'files'] as const;
export type ProjectSection = (typeof PROJECT_SECTIONS)[number];

const slug = encodeURIComponent;

export function jobsPath(project: string): string {
  return `/jobs/${slug(project)}`;
}

export function jobPath(project: string, env: string, name: string): string {
  return `/jobs/${slug(project)}/${slug(env)}/${slug(name)}`;
}

export function newJobPath(project: string, env: string, notebook?: string): string {
  const to = `/jobs/${slug(project)}/${slug(env)}/new`;
  return notebook ? `${to}?notebook=${encodeURIComponent(notebook)}` : to;
}

/**
 * The Connections area. No project in the path, and that is the point: a
 * connection belongs to the server, not to a repo — one list of shared ones and
 * each person's own, whichever project you happen to have been looking at.
 */
export function isFullBleed(pathname: string): boolean {
  return isEditorPath(pathname) || pathname.startsWith('/connections');
}

export function connectionsPath(id?: string): string {
  return id == null ? '/connections' : `/connections/${slug(id)}`;
}

export function filesPath(project: string): string {
  return `/files/${slug(project)}`;
}

/**
 * One notebook on one branch.
 *
 * The path goes last and is the only variable-length part, so every segment
 * before it has a fixed job: `edit` is a literal, the branch is exactly one
 * segment, and everything after is the file. Its separators stay separators —
 * encoding them would make `reports/monthly.nb.md` one unreadable segment, and
 * the router hands the tail back raw either way.
 */
export function editPath(
  project: string, branch: string, path: string, view: NotebookView = 'edit'): string {
  const parts = path.split('/').filter(Boolean).map(encodeURIComponent);
  return `/files/${slug(project)}/${view}/${slug(branch)}/${parts.join('/')}`;
}

/** The notebook path back out of a router splat, whatever it did to the escapes. */
export function pathFromSplat(splat: string | undefined): string {
  return (splat ?? '')
    .split('/')
    .filter(Boolean)
    .map((part) => {
      try {
        return decodeURIComponent(part);
      } catch {
        // A stray % is somebody's typo, not a reason to blank the editor.
        return part;
      }
    })
    .join('/');
}

/**
 * Where the project switcher goes when you pick a different project.
 *
 * The section, never the same page: the job or the file you are looking at is
 * this project's, and the other project's list is the honest answer to "show me
 * that one instead". Null off a project section, where switching means nothing
 * and the switcher is not drawn.
 */
export function switchProject(pathname: string, project: string): string | null {
  const section = sectionOf(pathname);
  return section == null ? null : `/${section}/${encodeURIComponent(project)}`;
}

/** Which project section a path is in, or null for the server-wide pages. */
export function sectionOf(pathname: string): ProjectSection | null {
  const first = pathname.split('/').filter(Boolean)[0];
  return PROJECT_SECTIONS.find((s) => s === first) ?? null;
}

/** Which reading of a notebook a path asks for, or null when it is not one. */
export function viewOf(pathname: string): NotebookView | null {
  const segments = pathname.split('/').filter(Boolean);
  if (segments[0] !== 'files' || segments.length < 5) {
    return null;
  }
  return NOTEBOOK_VIEWS.find((v) => v === segments[2]) ?? null;
}

/** True on the notebook editor, which lays its own panes out full height. */
export function isEditorPath(pathname: string): boolean {
  return viewOf(pathname) != null;
}

/**
 * The new home of a link written against the old query-string editor URL —
 * `/edit?project=…&path=…&branch=…`. Shared links and bookmarks predate the
 * move, and a dead link is a worse answer than a redirect.
 */
export function legacyEditPath(search: string): string {
  const params = new URLSearchParams(search);
  const path = params.get('path');
  const project = params.get('project') || 'default';
  const branch = params.get('branch') || 'mine';
  return path ? editPath(project, branch, path) : filesPath(project);
}
