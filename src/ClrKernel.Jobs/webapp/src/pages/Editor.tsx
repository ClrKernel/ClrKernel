import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { ApiError, api, projectSlug, setBranch, type ApiCell, type ApiLanguage } from '../api';
import { CellEditor, CellInserter, type RunMode } from '../components/CellEditor';
import { ConnectionWizard } from '../components/ConnectionWizard';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { ErrorBanner, usePolling } from '../components/common';
import { FocusMode } from '../components/FocusMode';
import { NotebookExplorer } from '../components/NotebookExplorer';
import { NotebookToolbar } from '../components/NotebookToolbar';
import { Splitter } from '../components/Splitter';
import { registerLanguageProviders } from '../monaco/language';
import { releaseCellModels } from '../monaco/models';
import {
  DEFAULT_LAYOUT,
  MAX_EXPLORER,
  MIN_EXPLORER,
  clamp,
  loadLayout,
  loadNotebookState,
  saveLayout,
  saveNotebookState,
  type LayoutPrefs,
} from '../prefs';
import { useDiffEditor, useFillEditor } from '../monaco/useMonaco';
import { BranchAllows, useCanWrite } from '../sessionContext';
import { useAutosave } from '../useAutosave';
import { neighbourCell } from '../toc';
import {
  cellsToRun,
  connectableLanguage,
  emptyCell,
  fileLanguage,
  insertCell,
  isDirty,
  keepIds,
  mergeStatus,
  moveCell,
  removeCell,
  setCellLanguage,
  toApiCells,
  toRunCells,
  toSyncCells,
  withIds,
  type EditorCell,
} from '../notebook';

type Tab = 'notebook' | 'source' | 'diff';


/**
 * The test notebook editor: cells with syntax highlighting and a language picker,
 * a raw-source escape hatch, and the diff that shows what promotion would ship.
 * Every save is a commit on the test branch — and a save that changes nothing is
 * skipped, because a needless commit invalidates the notebook's promotion
 * evidence.
 */
export function Editor() {
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const canWrite = useCanWrite();
  const path = search.get('path') ?? '';
  // Which branch you are looking at. Yours unless the link says otherwise.
  //
  // Only your own branch is writable — somebody else's is read-only to everybody
  // including admins, and so are test and prod. Those two are still *runnable*
  // though, which is the whole point of being able to open them: a job that died
  // at cell seven is finished by hand, not by editing production.
  const branch = search.get('branch') ?? 'mine';
  const allows = {
    write: branch === 'mine',
    run: branch === 'mine' || branch === 'test' || branch === 'prod',
  };
  // Mirrored into the API client rather than threaded through every call: the
  // kernel routes are reached from Monaco's providers as well as from here, and
  // handing each of them a branch means carrying one down through the model map.
  // Set during render, not in an effect, because the polls below start in effects
  // and would otherwise make their first request against the branch you left.
  setBranch(branch);
  const isNotebook = /\.(nb\.)?md$/i.test(path);

  const [cells, setCells] = useState<EditorCell[] | null>(null);
  const [saved, setSaved] = useState<ApiCell[]>([]);
  const [languages, setLanguages] = useState<ApiLanguage[]>([]);
  const [source, setSource] = useState<string | null>(null);
  const [savedSource, setSavedSource] = useState<string | null>(null);
  /** Bumped when the file changed underneath the editor — a merge, or a reload. */
  const [reloads, setReloads] = useState(0);
  /** Production's copy of this file: null while loading, '' when it has none. */
  const [prod, setProd] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>(isNotebook ? 'notebook' : 'source');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [pollFast, setPollFast] = useState(false);
  const [restartDismissed, setRestartDismissed] = useState(false);
  const [cleared, setCleared] = useState<Set<string>>(new Set());
  const [connectFor, setConnectFor] = useState<number | null>(null);
  // Focus Mode: one cell at a time. Per notebook, so switching files takes you
  // back to how you were working in that file.
  const [mode, setMode] = useState<'normal' | 'focus'>(() => loadNotebookState(path).mode ?? 'normal');
  const [activeId, setActiveId] = useState<string | null>(null);
  const [layout, setLayout] = useState<LayoutPrefs>(() => loadLayout());
  // The source each cell had when it was last run, so an edit can dim its output
  // instead of silently leaving a result that no longer matches the code.
  const ranSource = useRef<Record<string, string>>({});
  // The explorer's drag reports a viewport X; the sidebar's width is that minus
  // wherever this row actually starts, which is not the window's edge.
  const shell = useRef<HTMLDivElement>(null);

  // Where your branch stands against test. Polled slowly: it changes when you
  // save, push, or somebody else pushes — none of which is a per-second event.
  const { data: standing, reload: reloadStanding } = usePolling(
    () => api.branchStanding(),
    15000,
  );

  const { data: promotion, reload: reloadPromotion } = usePolling(
    () => api.promotionStatus(path),
    null,
    [path],
  );

  // Fast while a run is in flight, not at all when idle — polling a warm kernel
  // that is doing nothing is pure noise. TryStartRun takes its slot before the
  // 202 comes back, so the first poll after a run always sees it as running.
  const { data: session, error: sessionError, reload: reloadSession } = usePolling(
    () => api.sessionStatus(path),
    pollFast ? 400 : null,
    // The branch is part of which kernel this is: test and prod each get their
    // own, so switching branches has to ask again rather than keep showing the
    // status of the session you were looking at.
    [path, branch],
  );

  // The server is the authority in both directions. A click starts polling
  // optimistically; landing on a notebook that is already running — a refresh
  // mid-cell, or a second tab — has to start it too, or the buttons stay
  // disabled with nothing left to re-enable them.
  useEffect(() => {
    if (session) {
      setPollFast(session.running);
    }
  }, [session]);

  // IntelliSense, wired on the first session that answers. The trigger characters
  // are the kernel's own — Monaco fixes a provider's triggers at registration, so
  // this waits for the handshake rather than guessing and being wrong later. (The
  // VS Code client has the same constraint: its document selector is fixed when
  // the LanguageClient is constructed.)
  useEffect(() => {
    if (session?.started) {
      registerLanguageProviders(session.completionTriggers ?? [], session.signatureTriggers ?? []);
    }
  }, [session?.started, session?.completionTriggers, session?.signatureTriggers]);

  // Execution is gated server-side (git workflow, test only, and a key required
  // off localhost). A rejected status call is how the editor finds out — but a
  // transient failure after a good answer is not that.
  // Viewers never start a kernel: `canRun` is what every run control keys off,
  // and the server refuses the routes regardless.
  const canRun = canWrite && isNotebook && !(sessionError != null && session == null);
  const running = (session?.running ?? false) || pollFast;
  const runState = mergeStatus(cells ?? [], session, ranSource.current);

  const dirty = tab === 'source' || !isNotebook
    ? source != null && source !== savedSource
    : cells != null && isDirty(cells, saved);

  useEffect(() => {
    setError(null);
    api
      .notebookContent(branch, path)
      .then((text) => {
        setSource(text);
        setSavedSource(text);
      })
      .catch(() => setError(`Could not load ${path}.`));
    if (isNotebook) {
      api
        .notebookCells(branch, path)
        .then((result) => {
          setCells(withIds(result.cells));
          setSaved(result.cells);
          setLanguages(result.languages ?? []);
        })
        .catch((e) => setError((e as Error).message));
    }
    // The branch is part of what is being opened: switching to somebody else's
    // is opening a different file that happens to have the same name.
  }, [path, branch, isNotebook]);

  // Mode and layout are remembered, but nothing here ever reaches the notebook
  // file — how you were looking at it is not part of what it says.
  useEffect(() => {
    setMode(loadNotebookState(path).mode ?? 'normal');
    setActiveId(loadNotebookState(path).activeCellId ?? null);
  }, [path]);
  useEffect(() => saveLayout(layout), [layout]);

  // Focus Mode owns the viewport: the work area is fixed to it and each pane
  // scrolls on its own, so the page behind must not scroll as well.
  useEffect(() => {
    const on = mode === 'focus' && tab === 'notebook';
    document.body.classList.toggle('focus-mode-on', on);
    return () => document.body.classList.remove('focus-mode-on');
  }, [mode, tab]);

  // Leaving Focus Mode puts you back where you were in the list rather than at
  // the top — round-tripping should not cost you your place.
  useEffect(() => {
    if (mode !== 'normal' || activeId == null) {
      return;
    }
    const cell = document.querySelector(`[data-cell-id="${activeId}"]`);
    cell?.scrollIntoView({ block: 'center', behavior: 'auto' });
  }, [mode, activeId]);
  useEffect(() => saveNotebookState(path, { mode }), [path, mode]);
  useEffect(() => {
    if (activeId != null) {
      saveNotebookState(path, { activeCellId: activeId });
    }
  }, [path, activeId]);

  // The active cell has to exist. It may not on first load (a remembered id from
  // before an edit), and it stops existing when you delete it — in which case the
  // next cell takes over, or the previous one if it was last.
  useEffect(() => {
    if (cells == null || cells.length === 0) {
      return;
    }
    if (activeId == null || !cells.some((cell) => cell.id === activeId)) {
      setActiveId(cells[0].id);
    }
  }, [cells, activeId]);

  // Cell models belong to the notebook, so they are freed when a cell is deleted
  // and when the notebook is closed — not when an editor unmounts, which now
  // happens routinely without the cell going anywhere.
  useEffect(() => {
    if (cells != null) {
      releaseCellModels(cells.map((cell) => cell.id));
    }
  }, [cells]);
  useEffect(() => () => releaseCellModels([]), [path]);

  // Opening a notebook starts its kernel, the way opening one in VS Code starts the
  // language server. Without this a session appears only on the first run, and
  // completion would be dead until then — which is the wrong way round, since the
  // reason to want completion is that you have not run anything yet. Failures are
  // not reported here: the status poll below is what tells the user, once.
  useEffect(() => {
    if (isNotebook) {
      api.startSession(path).catch(() => undefined);
    }
  }, [path, branch, isNotebook]);

  // What the editor has open, told to the kernel on a debounce. Language features
  // answer about documents the server holds, so this is what makes them work at all
  // — and it is authoritative, so a deleted cell stops contributing its symbols.
  useEffect(() => {
    if (!isNotebook || cells == null || !canRun) {
      return;
    }
    let followUp: ReturnType<typeof setTimeout> | undefined;
    const timer = setTimeout(() => {
      api
        .syncCells(path, toSyncCells(cells))
        .then(() => {
          // Diagnostics are pushed to the server after it processes the change,
          // and there is no push channel on to the browser — so one status read
          // shortly after the sync is how the squiggles arrive. Deliberately not
          // a poll: it fires because something changed, and an idle notebook
          // stays silent. ponytail: SSE would make this a subscription and
          // delete the delay; it is the documented upgrade path.
          followUp = setTimeout(() => reloadSession(), 400);
        })
        .catch(() => undefined);
    }, 300);
    return () => {
      clearTimeout(timer);
      if (followUp != null) {
        clearTimeout(followUp);
      }
    };
  }, [path, branch, isNotebook, cells, canRun, reloadSession]);

  /**
   * Asks once before anything runs against production, naming what it is about to
   * touch. Once per notebook rather than per cell: driving a failed job home takes
   * a dozen runs, and a dialog on each of them is a dialog nobody reads. The side
   * effects are real even though the file is not being changed.
   */
  const confirmedProd = useRef(false);
  function confirmProduction(): boolean {
    if (branch !== 'prod' || confirmedProd.current) {
      return true;
    }
    const ok = confirm(
      `Run against PRODUCTION?\n\n`
      + `Project: ${projectSlug()}\nBranch: prod\nNotebook: ${path}\n\n`
      + 'Whatever this notebook does, it will really do.');
    confirmedProd.current = ok;
    return ok;
  }

  /**
   * The legitimate home for "I just need to tweak this one line".
   *
   * Copies what is on screen onto your own branch and opens it there — the rule
   * that test and prod are never edited only holds if the instinct to edit them
   * has somewhere to go.
   */
  async function copyToMine() {
    setError(null);
    setBusy(true);
    try {
      await api.saveNotebookContent(path, await api.notebookContent(branch, path));
      navigate(`/edit?project=${encodeURIComponent(projectSlug())}`
        + `&path=${encodeURIComponent(path)}`);
      setNotice(`Copied ${path} onto your branch.`);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  /** The commit moment: everything on your branch becomes one commit on test. */
  async function push(message: string) {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      await api.pushToTest(message);
      setNotice('Pushed to test.');
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
      reloadStanding();
    }
  }

  /** Merges test into your branch. Conflicts come back as files, never resolved. */
  async function updateFromTest() {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.updateFromTest();
      setNotice(result.merged
        ? 'Up to date with test.'
        : `Conflicts left in ${result.conflicts.join(', ')} — resolve the markers, save, then push.`);
      // The merge changed files under the editor; re-read rather than keep a
      // buffer that no longer matches what is on disk.
      const text = await api.notebookContent(branch, path);
      setSource(text);
      setSavedSource(text);
      setReloads((n) => n + 1);
      if (isNotebook) {
        const reloaded = await api.notebookCells(branch, path);
        setCells(keepIds(reloaded.cells, []));
        setSaved(reloaded.cells);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
      reloadStanding();
    }
  }

  /**
   * Writes the buffer to your branch. Called on a debounce while you type, and
   * again at the moments where losing the last few seconds would matter — see
   * `useAutosave`.
   *
   * It does not re-read afterwards. It used to, because a save was a commit and
   * the committed bytes were what mattered; now the file is a file and the buffer
   * is what you are looking at. Replacing it under a cursor every eight hundred
   * milliseconds would be its own kind of data loss.
   */
  const saveNow = useCallback(async (keepalive = false) => {
    if (tab === 'source' || !isNotebook) {
      const text = source ?? '';
      await api.saveNotebookContent(path, text, keepalive);
      setSavedSource(text);
    } else {
      await api.saveNotebookCells(path, toApiCells(cells ?? []), keepalive);
      setSaved(cells ?? []);
    }
  }, [path, tab, isNotebook, source, cells]);

  const { status: saveStatus, flush, retry } = useAutosave(
    // The buffer itself is the revision: every edit is a new value, which is what
    // restarts the debounce.
    tab === 'source' || !isNotebook ? source : cells,
    dirty,
    saveNow,
    () => {
      reloadPromotion();
      reloadStanding();
    },
  );

  /**
   * Runs a slice of the notebook against the warm kernel. What runs is decided
   * here and sent as an ordered list, so the server never learns which button
   * was pressed — one endpoint covers cell, above, below and all.
   */
  async function run(index: number, mode: RunMode | 'all') {
    const toRun = cellsToRun(cells ?? [], index, mode);
    if (toRun.length === 0) {
      setNotice('Nothing to run there — those cells are all prose.');
      return;
    }
    if (!confirmProduction()) {
      return;
    }
    setError(null);
    setNotice(null);
    try {
      // The cells go out in the request, so what runs is what you see whatever the
      // file says. Writing first anyway: a run is the moment you would most mind
      // discovering that the last thing you typed was still only in the browser.
      await flush();
      await api.runCells(path, toRunCells(toRun));
      for (const cell of toRun) {
        ranSource.current[cell.id] = cell.source;
      }
      // Running a cell replaces its output, so a previous "clear" no longer applies.
      setCleared((current) => {
        const next = new Set(current);
        for (const cell of toRun) {
          next.delete(cell.id);
        }
        return next;
      });
      setPollFast(true);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        // Expected, not a failure: the kernel runs one cell at a time.
        setNotice(e.message);
        setPollFast(true);
        return;
      }
      setError((e as Error).message);
    }
  }

  async function restartKernel() {
    setError(null);
    try {
      const { restarted } = await api.restartSession(path);
      // ponytail: outputs live only in the session, so a restart clears them.
      // Cache them client-side if keeping them across a restart ever matters.
      ranSource.current = {};
      setRestartDismissed(false);
      setNotice(
        restarted
          ? 'Kernel restarted — variables and cell outputs are cleared.'
          : 'No kernel was running for this notebook.',
      );
      reloadSession();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  /**
   * Both sides come from the content GET, which reads any environment — only
   * writing is test-only. A 404 on prod means the file exists solely on test, so
   * the original side is empty and the whole thing reads as added.
   */
  async function showDiff() {
    setTab('diff');
    setProd(null);
    try {
      setProd(await api.notebookContent('prod', path));
    } catch {
      setProd('');
    }
  }

  async function promote() {
    if (!confirm(`Promote ${path} to production?`)) {
      return;
    }
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.promote(path);
      setNotice(
        `Promoted to production (${result.commitSha.slice(0, 8)}). The prod scheduler picks it up on its next tick.`,
      );
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function update(index: number, change: (cell: EditorCell) => EditorCell) {
    setCells((current) => current?.map((cell, i) => (i === index ? change(cell) : cell)) ?? current);
  }

  function insertAt(index: number, kind: 'code' | 'markdown') {
    setCells((current) => insertCell(current ?? [], index, emptyCell(kind)));
  }

  /**
   * A connect directive becomes its own cell above the one you opened the wizard
   * from, in that cell's language — a connection is a statement about the session,
   * not part of the query you were writing.
   */
  function insertConnection(index: number, directive: string) {
    const source = cells?.[index];
    setCells((current) =>
      insertCell(current ?? [], index, {
        ...emptyCell('code'),
        tag: source?.tag ?? null,
        languageId: source?.languageId ?? null,
        source: directive,
      }),
    );
    setConnectFor(null);
    setNotice('Connection cell added. Run it to open the connection.');
  }

  const focusing = mode === 'focus' && tab === 'notebook';
  // Source and Diff are whole files, not a column of cells: they take the height
  // of the pane and scroll inside themselves, so the page must not scroll too.
  const fills = focusing || tab === 'source' || tab === 'diff';

  return (
    // Somebody else's branch reads exactly like your own and changes in none of
    // the same ways: one flag at the top rather than a `disabled` on every control.
    <BranchAllows.Provider value={allows}>
    {/* The editor is the one page with its own sidebar: file tree on the left,
        toolbar and work area on the right. */}
    <div className="flex min-h-0 flex-1 overflow-hidden" ref={shell}>
      <NotebookExplorer
        path={path}
        branch={branch}
        width={layout.explorerWidth}
        collapsed={layout.explorerCollapsed}
        onCollapse={(explorerCollapsed) => setLayout({ ...layout, explorerCollapsed })}
      />
      {!layout.explorerCollapsed && (
        <Splitter
          orientation="vertical"
          label="Explorer width"
          onDrag={(clientX) =>
            setLayout({
              ...layout,
              explorerWidth: clamp(
                clientX - (shell.current?.getBoundingClientRect().left ?? 0),
                MIN_EXPLORER,
                MAX_EXPLORER,
              ),
            })
          }
          onReset={() => setLayout({ ...layout, explorerWidth: DEFAULT_LAYOUT.explorerWidth })}
        />
      )}
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
      <NotebookToolbar
        tab={tab}
        onTab={(next) => (next === 'diff' ? showDiff() : setTab(next as Tab))}
        isNotebook={isNotebook}
        canRun={canRun}
        running={running}
        session={session}
        mode={mode}
        onMode={setMode}
        onRunAll={() => run(0, 'all')}
        onRestart={restartKernel}
        saveStatus={saveStatus}
        busy={busy}
        onSave={retry}
        onPromote={promote}
        promotion={promotion}
        standing={standing}
        onPush={push}
        onUpdate={updateFromTest}
        branch={branch}
        onCopyToMine={copyToMine}
      />

      {/* Focus Mode measures itself to the bottom of this scroller and gives
          back whatever overflows, so padding below it is viewport it cannot
          use. Every other view wants the gutter. */}
      <div className={fills ? 'flex min-h-0 flex-1 flex-col' : 'min-h-0 flex-1 overflow-auto pb-8'}>
      <div className="empty:hidden px-4 pt-3">
        <ErrorBanner error={error} />
        {notice && (
          <Alert variant="success" className="mb-3">
            <AlertDescription className="text-status-success">{notice}</AlertDescription>
          </Alert>
        )}

        {/* "Not promotable yet" used to live here as a permanent banner. It is
            now the info button beside Promote in the toolbar: the reasons do not
            change while you work, so re-reading them every time you scroll past
            is cost without information. */}
      </div>

      {tab === 'notebook' &&
        (cells == null ? (
          <p className="px-4 text-base text-muted-foreground">Loading…</p>
        ) : (
          <div className="notebook-editor">
            {/* Run All, Restart, the kernel badge and the mode toggle all live
                in the one toolbar now. What is left here is the notices, which
                are about this notebook rather than about the page. */}
            {!canRun && isNotebook && (
              <p className="px-4 text-base text-muted-foreground">
                Running cells is unavailable here: {sessionError}
              </p>
            )}

            {session?.scheduledRunActive && (
              <Alert variant="warning" className="mx-4 mb-3 w-auto">
                <AlertDescription>
                  A scheduled run of this notebook is in flight. It executes in its own kernel from
                  the committed file, so what you run here does not affect it — but saving now
                  changes what the <em>next</em> run picks up.
                </AlertDescription>
              </Alert>
            )}

            {session?.kernelRestarted && !restartDismissed && (
              <Alert variant="warning" className="mx-4 mb-3 w-auto">
                <AlertDescription className="flex flex-wrap items-center gap-2">
                  <span>
                    The kernel exited on its own and was replaced — variables from earlier cells
                    are gone. Re-run the cells you need.
                  </span>
                  <Button
                    variant="outline"
                    size="sm"
                    className="h-6 px-2 text-sm"
                    onClick={() => setRestartDismissed(true)}
                  >
                    Dismiss
                  </Button>
                </AlertDescription>
              </Alert>
            )}

            {mode === 'focus' ? (
              <FocusMode
                cells={cells}
                path={path}
                languages={languages}
                runState={runState}
                activeId={activeId}
                canRun={canRun}
                busy={running}
                cleared={cleared}
                layout={layout}
                onActivate={setActiveId}
                onChange={(cellId, value) =>
                  setCells((current) =>
                    (current ?? []).map((c) => (c.id === cellId ? { ...c, source: value } : c)))}
                onLanguage={(cellId, value) =>
                  setCells((current) =>
                    (current ?? []).map((c) =>
                      c.id === cellId ? setCellLanguage(c, value, languages) : c))}
                onRun={(cellId) => run(cells.findIndex((c) => c.id === cellId), 'one')}
                onClearOutput={(cellId) => setCleared((current) => new Set(current).add(cellId))}
                onLayout={setLayout}
                onDelete={(cellId) => {
                  const index = cells.findIndex((c) => c.id === cellId);
                  const remaining = removeCell(cells, index);
                  setCells(remaining);
                  setActiveId(neighbourCell(remaining, index));
                }}
                onInsert={(afterId, kind) => {
                  const at = afterId == null ? 0 : cells.findIndex((c) => c.id === afterId) + 1;
                  const cell = emptyCell(kind);
                  setCells(insertCell(cells, at, cell));
                  setActiveId(cell.id);
                }}
              />
            ) : (
              <div className="px-4">
            <CellInserter always={cells.length === 0} onInsert={(kind) => insertAt(0, kind)} />
            {cells.map((cell, index) => (
              // The reload counter is in the key so a file replaced under the
              // editor — a merge from test — redraws its cells rather than
              // leaving Monaco holding the text you had before it.
              <div key={`${reloads}:${cell.id}`} data-cell-id={cell.id}>
                <CellEditor
                  cell={cell}
                  index={index}
                  count={cells.length}
                  languages={languages}
                  path={path}
                  diagnostics={session?.diagnostics?.[cell.id]}
                  run={runState[cell.id] ?? null}
                  canRun={canRun}
                  busy={running}
                  onChange={(value) => update(index, (c) => ({ ...c, source: value }))}
                  onLanguage={(value) => update(index, (c) => setCellLanguage(c, value, languages))}
                  onMove={(to) => setCells((current) => (current ? moveCell(current, index, to) : current))}
                  onDelete={() => setCells((current) => (current ? removeCell(current, index) : current))}
                  onRun={(mode) => run(index, mode)}
                  cleared={cleared.has(cell.id)}
                  onClearOutput={() => setCleared((current) => new Set(current).add(cell.id))}
                  onConnect={() => setConnectFor(index)}
                />
                <CellInserter
                  always={index === cells.length - 1}
                  onInsert={(kind) => insertAt(index + 1, kind)}
                />
              </div>
            ))}
              </div>
            )}
          </div>
        ))}

      {tab === 'source' && (
        <div className="flex min-h-0 flex-1 flex-col px-4 pb-4">
          {source == null ? (
            <p className="text-base text-muted-foreground">Loading…</p>
          ) : (
            <SourceEditor
              value={source}
              language={fileLanguage(path)}
              onChange={setSource}
              resetKey={reloads}
            />
          )}
        </div>
      )}

      {tab === 'diff' && (
        <div className="flex min-h-0 flex-1 flex-col px-4 pb-4">
          {prod == null || savedSource == null ? (
            <p className="text-base text-muted-foreground">Loading…</p>
          ) : prod === savedSource ? (
            <p className="text-base text-muted-foreground">
              No differences — test and production are identical for this file.
            </p>
          ) : (
            <>
              <p className="mb-2 max-w-[78ch] shrink-0 text-base text-muted-foreground">
                Production (left) vs test (right)
                {prod === '' && ' — this file does not exist in production yet'}
                {'. Your own branch is not in this: it compares what is committed on '}
                {'each of the two branches that run.'}
              </p>
              <DiffView original={prod} modified={savedSource} language={fileLanguage(path)} />
            </>
          )}
        </div>
      )}

      {connectFor != null && cells?.[connectFor] &&
        connectableLanguage(cells[connectFor].languageId, languages) && (
          <ConnectionWizard
            path={path}
            language={connectableLanguage(cells[connectFor].languageId, languages)!}
            onInsert={(directive) => insertConnection(connectFor, directive)}
            onClose={() => setConnectFor(null)}
          />
        )}

      {/* The footnote that used to sit here — what a save commits, and why an
          interactive run is not promotion evidence — is the info button beside
          Save in the toolbar. It answered a question nobody was asking most of
          the time, and it cost the bottom of every notebook to do it. */}
      </div>
      </div>
    </div>
    </BranchAllows.Provider>
  );
}

/** The whole file as one editor — the fallback for non-notebooks, and the escape
 *  hatch when you want to see exactly what is on disk. */
function SourceEditor({
  value, language, onChange, resetKey,
}: {
  value: string;
  language: string;
  onChange: (value: string) => void;
  /** Changes when the file was replaced under the editor rather than typed into. */
  resetKey: number;
}) {
  const container = useFillEditor(language, value, onChange, !useCanWrite(), resetKey);
  return <div className="source-editor" ref={container} />;
}

/** What promotion would ship, side by side — the same view VS Code gives a
 *  branch comparison, rather than a unified diff to read in your head. */
function DiffView({
  original, modified, language,
}: {
  original: string;
  modified: string;
  language: string;
}) {
  const container = useDiffEditor(original, modified, language, true);
  return <div className="diff-editor" ref={container} />;
}
