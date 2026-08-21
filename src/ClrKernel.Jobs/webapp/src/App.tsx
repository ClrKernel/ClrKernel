import { useState } from 'react';
import { Route, Routes, useLocation } from 'react-router-dom';
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
import { AccentContext, applyAccent, loadAccent } from './theme/accent';
import { ACCENTS } from './theme/palette';

export function App() {
  // The inline script in index.html already put the accent on <html> before
  // first paint; this only mirrors it so the picker can show a tick.
  const [accent, setAccent] = useState(loadAccent);
  const isEditor = useLocation().pathname === '/edit';

  const accentValue = ACCENTS.find((a) => a.name === accent) ?? ACCENTS[0];

  return (
    <AccentContext.Provider value={accentValue}>
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
                ? 'min-h-0 flex-1 overflow-auto'
                : 'min-h-0 flex-1 overflow-auto px-6 pb-8 pt-4'
            }
          >
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/jobs" element={<Jobs />} />
              <Route path="/jobs/:env/new" element={<JobDetail />} />
              <Route path="/jobs/:env/:name" element={<JobDetail />} />
              <Route path="/notebooks" element={<Notebooks />} />
              <Route path="/channels" element={<Channels />} />
              <Route path="/settings" element={<Settings />} />
              <Route path="/edit" element={<Editor />} />
              <Route path="/runs/:id" element={<RunDetail />} />
              <Route path="*" element={<p className="text-base text-muted-foreground">Not found.</p>} />
            </Routes>
          </main>
        </div>
      </div>
      <Toaster position="bottom-right" richColors closeButton />
    </TooltipProvider>
    </AccentContext.Provider>
  );
}
