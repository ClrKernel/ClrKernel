import { useCallback, useEffect, useState } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { Toaster } from '@/components/ui/sonner';
import { TooltipProvider } from '@/components/ui/tooltip';
import { Rail } from './components/Rail';
import { TopBar } from './components/TopBar';
import { Channels } from './pages/Channels';
import { Dashboard } from './pages/Dashboard';
import { Editor } from './pages/Editor';
import { JobDetail } from './pages/JobDetail';
import { Jobs } from './pages/Jobs';
import { Notebooks } from './pages/Notebooks';
import { RunDetail } from './pages/RunDetail';
import { Settings } from './pages/Settings';
import { Invite } from './pages/Invite';
import { SignIn, Setup } from './pages/SignIn';
import { ProjectProvider, ProjectScope } from './projectContext';
import { loadSession, type SessionState } from './auth';
import { SessionContext } from './sessionContext';
import { AccentContext, applyAccent, loadAccent } from './theme/accent';
import { ACCENTS } from './theme/palette';

export function App() {
  // The inline script in index.html already put the accent on <html> before
  // first paint; this only mirrors it so the picker can show a tick.
  const [accent, setAccent] = useState(loadAccent);
  const location = useLocation();
  const navigate = useNavigate();
  const isEditor = location.pathname === '/edit';
  const [session, setSession] = useState<SessionState | null>(null);

  // Returns the session it loaded, because arriving from a sign-in has to wait
  // for it: navigating while the old state still says "not signed in" bounces
  // straight back out through the signed-out routes below.
  const refresh = useCallback(
    () => loadSession().then((next) => {
      setSession(next);
      return next;
    }).catch(() => {
      setSession(null);
      return null;
    }),
    [],
  );
  useEffect(() => {
    refresh();
  }, [refresh]);

  const accentValue = ACCENTS.find((a) => a.name === accent) ?? ACCENTS[0];

  // The server redirects a signed-out browser here, so these routes render
  // without the app shell — there is no breadcrumb to show and no rail to
  // navigate with until you are somebody.
  const anonymousPage = ['/signin', '/setup'].includes(location.pathname)
    || location.pathname.startsWith('/invite/');
  // Signing in from one of those pages leaves you standing on it. Sending an
  // authenticated session to the app is what makes the redirects below safe to
  // write as plain rules rather than as a race against the session refresh.
  if (anonymousPage && session?.authenticated === true && !session.needsSetup) {
    return <Navigate to="/" replace />;
  }
  if (anonymousPage || (session != null && !session.authenticated)) {
    // The navigation waits for the new session: `session` still says
    // unauthenticated at this point, and moving to / before it updates would
    // re-enter this branch and redirect back to /setup or /signin.
    const arrive = () => {
      refresh().then(() => navigate('/', { replace: true }));
    };
    return (
      <AccentContext.Provider value={accentValue}>
        <Routes>
          {/* Claimed already: /setup is not a page any more, and the server 404s
              it regardless. */}
          <Route
            path="/setup"
            element={
              session != null && !session.needsSetup
                ? <Navigate to="/signin" replace />
                : <Setup session={session} onSignedIn={arrive} />
            }
          />
          <Route path="/invite/:code" element={<Invite session={session} onSignedIn={arrive} />} />
          {/* An unclaimed server sends every other door to /setup. The server does
              this too, but only for documents it serves itself — under `npm run
              dev` the page comes from Vite on another port and only /api is
              proxied, so the redirect has to happen here as well. */}
          <Route
            path="*"
            element={
              session?.needsSetup
                ? <Navigate to="/setup" replace />
                : <SignIn session={session} onSignedIn={arrive} />
            }
          />
        </Routes>
        <Toaster position="bottom-right" richColors closeButton />
      </AccentContext.Provider>
    );
  }

  // The very first paint, before /api/auth/session has answered. Rendering the
  // shell here would flash a signed-in app at someone who is not.
  if (session == null) {
    return <div className="min-h-screen bg-background" />;
  }

  return (
    <SessionContext.Provider value={session}>
    <AccentContext.Provider value={accentValue}>
    <ProjectProvider>
    <TooltipProvider delayDuration={300}>
      {/* Fixed rail, fixed top bar, scrolling content — the page itself never
          scrolls, so the chrome cannot slide away under a long notebook.

          The row track is minmax(0,1fr), not the implicit `auto`: an auto row
          grows to its content, so h-screen would only clip the overflow rather
          than constrain it, and a long notebook would push the content region
          to several times the viewport height. */}
      <div className="grid h-screen grid-cols-[48px_1fr] grid-rows-[minmax(0,1fr)] overflow-hidden">
        <Rail />
        <div className="flex min-h-0 min-w-0 flex-col">
          <TopBar
            accent={accent}
            onAccent={(next) => {
              applyAccent(next);
              setAccent(next);
            }}
          />
          <main
            // The editor manages its own panes and gutters; every other page
            // takes the standard content padding.
            className={
              isEditor
                ? 'flex min-h-0 flex-1 flex-col overflow-hidden'
                : 'min-h-0 flex-1 overflow-auto px-7 py-5'
            }
          >
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/jobs" element={<Jobs />} />
              {/* The project is in the path because a link to a job has to mean
                  one job — two projects may each have a `nightly`. */}
              <Route path="/jobs/:project/:env/new" element={<ProjectScope><JobDetail /></ProjectScope>} />
              <Route path="/jobs/:project/:env/:name" element={<ProjectScope><JobDetail /></ProjectScope>} />
              <Route path="/notebooks" element={<Notebooks />} />
              <Route path="/channels" element={<Channels />} />
              {/* Settings is tabbed by route: /settings redirects to the first
                  section, and each section is its own URL so a tab is something
                  you can link to. */}
              <Route path="/settings" element={<Settings />} />
              <Route path="/settings/:section" element={<Settings />} />
              <Route path="/edit" element={<ProjectScope><Editor /></ProjectScope>} />
              <Route path="/runs/:id" element={<RunDetail />} />
              <Route
              path="*"
              element={<p className="text-base text-muted-foreground">Not found.</p>}
            />
            </Routes>
          </main>
        </div>
      </div>
      <Toaster position="bottom-right" richColors closeButton />
    </TooltipProvider>
    </ProjectProvider>
    </AccentContext.Provider>
    </SessionContext.Provider>
  );
}
