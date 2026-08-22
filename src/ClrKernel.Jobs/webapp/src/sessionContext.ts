import { createContext, useContext } from 'react';
import type { SessionState } from './auth';

/**
 * Who is signed in, for the whole app.
 *
 * `useCanWrite()` is what components ask. It hides controls a Server Viewer
 * cannot use — but the boundary is the server's: every route that writes or
 * executes checks the role again, because a hidden button is a courtesy and not
 * a permission system.
 */
export const SessionContext = createContext<SessionState | null>(null);

export function useSession(): SessionState | null {
  return useContext(SessionContext);
}

export function useCanWrite(): boolean {
  return useContext(SessionContext)?.user?.role === 'ServerAdmin';
}
