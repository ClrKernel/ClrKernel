import { createContext, useContext } from 'react';
import type { ProjectRole } from './api';
import type { SessionState } from './auth';
import { useProjects } from './projectContext';

/**
 * Who is signed in, for the whole app.
 *
 * `useCanWrite()` is what components ask. It hides controls a Server Viewer
 * cannot use — but the boundary is the server's: every route that writes or
 * executes checks the role again, because a hidden button is a courtesy and not
 * a permission system.
 */
export const SessionContext = createContext<SessionState | null>(null);

/**
 * What the branch on screen allows, layered onto what the role allows.
 *
 * The two are not the same question. test and prod are read-only for everybody and
 * still runnable — when a scheduled job dies at cell seven, the fix is to run the
 * rest, not to edit production — so a page asks `useCanWrite()` about editing and
 * `useCanRun()` about running, and neither has to know which branch it is on.
 */
export const BranchAllows = createContext({ write: true, run: true });

export function useSession(): SessionState | null {
  return useContext(SessionContext);
}

const ORDER: ProjectRole[] = ['ProjectViewer', 'ProjectMember', 'ProjectAdmin'];

function atLeast(role: ProjectRole | null | undefined, minimum: ProjectRole): boolean {
  return role != null && ORDER.indexOf(role) >= ORDER.indexOf(minimum);
}

/**
 * May edit and run in the project you are looking at.
 *
 * Project-relative rather than server-wide: the same account can own a branch in
 * one project and be a stranger to the next. The server checks again on every
 * route — this only decides which controls are worth drawing.
 */
export function useCanWrite(): boolean {
  return useContext(BranchAllows).write && atLeast(useProjectRole(), 'ProjectMember');
}

/**
 * May run cells here. A Project Member in test, a Project Admin in prod, either on
 * their own branch — and the server checks again on every one of those.
 */
export function useCanRun(): boolean {
  return useContext(BranchAllows).run && atLeast(useProjectRole(), 'ProjectMember');
}

/** May configure this project, manage its members, and promote to production. */
export function useIsProjectAdmin(): boolean {
  return atLeast(useProjectRole(), 'ProjectAdmin');
}

/** Server-wide administration: accounts, settings, channels, registering projects. */
export function useIsServerAdmin(): boolean {
  return useContext(SessionContext)?.user?.role === 'ServerAdmin';
}

function useProjectRole(): ProjectRole | undefined {
  const { projects, current } = useProjects();
  return projects.find((p) => p.slug === current)?.role;
}
