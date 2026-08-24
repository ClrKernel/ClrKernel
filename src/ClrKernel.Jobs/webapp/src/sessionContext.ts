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
 * Set where the thing on screen is not yours to change — browsing somebody else's
 * branch, or looking at test.
 *
 * Layered onto the role rather than replacing it, so a page does not have to ask
 * two questions: `useCanWrite()` already means "may I change what I am looking
 * at", and this is the other half of that sentence.
 */
export const ReadOnlyContext = createContext(false);

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
  const readOnly = useContext(ReadOnlyContext);
  return !readOnly && atLeast(useProjectRole(), 'ProjectMember');
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
