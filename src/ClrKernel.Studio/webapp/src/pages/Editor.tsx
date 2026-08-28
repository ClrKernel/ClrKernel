import { useCallback, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import {
  ApiError, api, projectSlug, setBranch,
  type ApiCell, type ApiJobsProblem, type ApiLanguage,
} from '../api';
import { CellEditor, CellInserter, type RunMode } from '../components/CellEditor';
import { ConnectionWizard } from '../components/ConnectionWizard';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { ErrorBanner, usePolling } from '../components/common';
import { FocusMode } from '../components/FocusMode';
import { NotebookExplorer } from '../components/NotebookExplorer';
import { JobsOverview } from '../components/JobsOverview';
import { NotebookToolbar } from '../components/NotebookToolbar';
import { moveNotebookTo, saveNotebookAs } from '../newNotebook';
import { Splitter } from '../components/Splitter';
import { registerLanguageProviders } from '../monaco/language';
import { monaco } from '../monaco/setup';
import { enableJobsSchema } from '../monaco/yamlSchema';
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
import { editPath, pathFromSplat, viewOf, type NotebookView } from '../routes';
import { neighbourCell } from '../toc';
import {
  cellsToRun,
  connectableLanguage,
  copyOfCell,
  emptyCell,
  fileEditable,
  fileLanguage,
  isJobsFile,
  notebookPaths,
  insertCell,
  isDirty,
  keepIds,
  mergeStatus,
  moveCell,
  pushUndo,
  removeCell,
  restoreCells,
  setCellLanguage,
  toApiCells,
  toRunCells,
  toSyncCells,
  withIds,
  type EditorCell,
} from '../notebook';




/**
 * The test notebook editor: cells with syntax highlighting and a language picker,
 * a raw-source escape hatch, and the diff that shows what promotion would ship.
 * Every save is a commit on the test branch — and a save that changes nothing is
 * skipped, because a needless commit invalidates the notebook's promotion
 * evidence.
 */
export function Editor() {
  // /files/:project/edit/:branch/*path — the notebook is the splat because it is
  // the only part that can be any number of segments deep.
  const params = useParams<{ project: string; branch: string; '*': string }>();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const canWrite = useCanWrite();
  const path = pathFromSplat(params['*']);
  // Which branch you are looking at.
  //
  // Only your own branch is writable — somebody else's is read-only to everybody
  // including admins, and so are test and prod. Those two are still *runnable*
  // though, which is the whole point of being able to open them: a job that died
  // at cell seven is finished by hand, not by editing production.
  const branch = params.branch ?? 'mine';
  const allows = {
    // The file type as well as the branch: Files lists everything in the project
    // now, and a `.txt` opens to be read. Offering Save on one would be offering
    // a write the server refuses — `NotebookTree.IsEditable` is the same rule.
    write: branch === 'mine' && fileEditable(path),
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
  const [privateConnections, setPrivateConnections] = useState<string[]>([]);
  /**
   * The saved connection this notebook queries, for schema completion in its SQL
   * cells. The notebook names a connection; the id is this reader's lookup of that
   * name, so a notebook shared between people completes against each of their own
   * view of it — which is the same connection when it is shared, and nothing at all
   * when it is somebody else's private one.
   */
  const [connectionId, setConnectionId] = useState<string | null>(null);
  /** Its `$type`, which decides which dialects can run on it. Separate from the
   *  id because the id is only useful for a connection this server can open, and
   *  compatibility is a fact about the connection either way. */
  const [connectionType, setConnectionType] = useState<string | null>(null);
  const [saved, setSaved] = useState<ApiCell[]>([]);
  const [languages, setLanguages] = useState<ApiLanguage[]>([]);
  const [source, setSource] = useState<string | null>(null);
  const [savedSource, setSavedSource] = useState<string | null>(null);
  /** Bumped when the file changed underneath the editor — a merge, or a reload. */
  const [reloads, setReloads] = useState(0);
  /** Production's copy of this file: null while loading, '' when it has none. */
  const [prod, setProd] = useState<string | null>(null);
  // Which reading of the file you asked for. In the URL rather than in state, so
  // it survives a reload, a bookmark and the back button — and so switching is a
  // navigation, which is what makes re-reading the file on the way in the
  // obvious thing to do rather than an extra mechanism.
  //
  // Only a `.nb.md` parses into cells, so `edit` on anything else is a view that
  // cannot render; the URL is corrected to match what is actually shown.
  const asked = viewOf(pathname) ?? 'edit';
  // A view that does not belong to this kind of file falls back to the one every
  // file has. Arriving at `/edit/` on a jobs file, or `/overview/` on a notebook,
  // is a link from elsewhere rather than a choice — Source is the honest answer.
  const jobsFile = isJobsFile(path);
  const tab: NotebookView =
    (asked === 'edit' && !isNotebook) || (asked === 'overview' && !jobsFile) ? 'source' : asked;
  useEffect(() => {
    if (tab !== asked) {
      navigate(editPath(projectSlug(), branch, path, tab), { replace: true });
    }
  }, [tab, asked, branch, path, navigate]);
  // What the server said about this file when it was last saved. Jobs files only;
  // it stays empty for everything else.
  const [jobsProblems, setJobsProblems] = useState<ApiJobsProblem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [pollFast, setPollFast] = useState(false);
  const [restartDismissed, setRestartDismissed] = useState(false);
  const [cleared, setCleared] = useState<Set<string>>(new Set());
  const [connectFor, setConnectFor] = useState<number | null>(null);
  /**
   * The cell clipboard, and the structural history.
   *
   * Both belong to the editor rather than to a notebook: this component stays
   * mounted while you move between files, so a cell copied in one pastes into
   * the next — which is most of the reason to want cut and paste at all. The
   * history is cleared on load, because a snapshot of another file is not
   * something Ctrl+Z should be able to reach.
   */
  const [clipboard, setClipboard] = useState<EditorCell | null>(null);
  const [undoStack, setUndoStack] = useState<EditorCell[][]>([]);
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
    // Only where a kernel is a thing that may exist. Somebody else's branch has no
    // session anybody may ask about, and asking is a refusal in the console and a
    // notice on the page telling a reader off for reading.
    () => (allows.run ? api.sessionStatus(path) : Promise.resolve(null)),
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
  //
  // `canWrite` here is the *role*, not the branch: this component renders the
  // BranchAllows provider, so its own hook call reads the value from outside it.
  // Which branch may run is `allows.run`, and the controls themselves ask
  // useCanRun() from inside.
  const canRun = canWrite && isNotebook && !(sessionError != null && session == null);
  const running = (session?.running ?? false) || pollFast;
  const runState = mergeStatus(cells ?? [], session, ranSource.current);

  // Source is the buffer on the Source tab and on anything that is not a
  // notebook; everywhere else the cells are.
  // Overview edits the same text `source` holds — it is a form over the file, not
  // a second model — so it saves, diffs and dirties through exactly this path.
  const editingText = tab === 'source' || tab === 'overview' || !isNotebook;
  const dirty = editingText
    ? source != null && source !== savedSource
    : cells != null && isDirty(cells, saved);

  /**
   * Reads the file for the view you are on — and re-reads it every time you
   * switch to one.
   *
   * The cells and the text are the same file read two ways, and each goes stale
   * the moment you edit through the other. It used to read both once, on open,
   * so switching showed you the file as it was when you arrived: the visible
   * half of that was a stale Source tab, and the other half was autosave
   * writing the load-time cells back over text you had typed since. Reading on
   * the way in is what makes the two agree, and switching is a navigation now,
   * so there is a moment to do it in.
   *
   * The branch and the path are in here for the same reason: opening somebody
   * else's branch is opening a different file that happens to share a name.
   */
  useEffect(() => {
    let live = true;
    setError(null);
    if (tab === 'edit') {
      api
        .notebookCells(branch, path)
        .then((result) => {
          if (!live) {
            return;
          }
          setCells(withIds(result.cells));
          setSaved(result.cells);
          setUndoStack([]);
          setLanguages(result.languages ?? []);
          setPrivateConnections(result.privateConnections ?? []);
          void resolveConnection(result.connections ?? []).then((found) => {
            if (live) {
              setConnectionId(found.id);
              setConnectionType(found.providerType);
            }
          });
          setReloads((n) => n + 1);
        })
        .catch((e) => live && setError((e as Error).message));
    } else {
      api
        .notebookContent(branch, path)
        .then((text) => {
          if (!live) {
            return;
          }
          setSource(text);
          setSavedSource(text);
          setReloads((n) => n + 1);
        })
        .catch(() => live && setError(`Could not load ${path}.`));
    }
    // Two switches in quick succession are two requests, and they can come back
    // in either order. Whichever one is no longer the view on screen drops its
    // answer rather than writing it into state.
    return () => {
      live = false;
    };
  }, [path, branch, tab]);

  // Mode and layout are remembered, but nothing here ever reaches the notebook
  // file — how you were looking at it is not part of what it says.
  // Which cell is *not* restored here: it is remembered as a position, and a
  // position means nothing until the cells it indexes have arrived. The effect
  // below owns it. Doing it here would resolve the new notebook's remembered
  // position against the old notebook's cells, which is how you land on a cell
  // from the file you just left.
  useEffect(() => {
    setMode(loadNotebookState(path).mode ?? 'normal');
  }, [path]);
  useEffect(() => saveLayout(layout), [layout]);

  /**
   * Ctrl/⌘+Z, for the structural history.
   *
   * Only when the keystroke did not land in an editor: inside a cell, Ctrl+Z is
   * Monaco's text undo and taking it would make the two undos fight over one
   * key. That is the same split VS Code makes, and it is why this listens on the
   * document rather than binding a Monaco command.
   *
   * Deliberately no Ctrl+X/C/V for cells. Those are the text clipboard
   * everywhere else on this page, and without a cell-selection mode — a notebook
   * where a cell can be selected *without* its editor being focused — there is no
   * moment when they are unambiguously about the cell rather than about the code
   * in it. The menu says which one it means.
   */
  useEffect(() => {
    if (tab !== 'edit' || !allows.write) {
      return;
    }
    function onKey(event: KeyboardEvent) {
      const undoKey = (event.metaKey || event.ctrlKey)
        && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 'z';
      if (!undoKey) {
        return;
      }
      const target = event.target as HTMLElement | null;
      if (target?.closest('.monaco-editor, input, textarea, [contenteditable="true"]') != null) {
        return;
      }
      event.preventDefault();
      undo();
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
    // `undo` closes over both, so the listener is replaced whenever either moves.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, allows.write, undoStack, cells]);

  // Focus Mode owns the viewport: the work area is fixed to it and each pane
  // scrolls on its own, so the page behind must not scroll as well.
  useEffect(() => {
    const on = mode === 'focus' && tab === 'edit';
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
  /**
   * Both preferences are written when you change them, never from an effect that
   * watches them.
   *
   * An effect keyed on `[path, mode]` runs on the render where the path has
   * changed and the state has not, so it writes the file you left's answer under
   * the file you arrived at. That was survivable for the mode by luck — the
   * effect that restores it is declared first, so it read the old value before
   * the write landed — and it is not the sort of thing to leave standing.
   */
  function chooseMode(next: 'normal' | 'focus') {
    setMode(next);
    saveNotebookState(path, { mode: next });
  }

  function activate(cellId: string) {
    setActiveId(cellId);
    const at = (cells ?? []).findIndex((cell) => cell.id === cellId);
    if (at >= 0) {
      saveNotebookState(path, { activeCell: at });
    }
  }

  // The active cell has to exist. It does not when the notebook has just been
  // read — the cells are new objects with new ids — and it stops existing when
  // you delete it. Either way this puts you back at the position you were last
  // on in this file, clamped to what the file now has: a notebook that lost
  // cells while you were away lands you on its last one rather than nowhere.
  useEffect(() => {
    if (cells == null || cells.length === 0) {
      return;
    }
    if (activeId != null && cells.some((cell) => cell.id === activeId)) {
      return;
    }
    const wanted = loadNotebookState(path).activeCell ?? 0;
    setActiveId(cells[clamp(wanted, 0, cells.length - 1)].id);
  }, [cells, activeId, path]);

  // Cell models belong to the notebook, so they are freed when a cell is deleted
  // and when the notebook is closed — not when an editor unmounts, which now
  // happens routinely without the cell going anywhere.
  useEffect(() => {
    if (cells != null) {
      releaseCellModels(cells.map((cell) => cell.id));
    }
  }, [cells]);
  useEffect(() => () => releaseCellModels([]), [path]);

  // The branch's notebooks, so the Overview form can say when a job names one
  // that is not here yet — the failure that otherwise surfaces as a scheduled run
  // dying at midnight. Only fetched on that tab; nothing else needs the tree.
  const { data: trees } = usePolling(
    () => (tab === 'overview' ? api.notebooks() : Promise.resolve(null)), null);
  const branchNotebooks = notebookPaths(
    trees?.environments.find((e) => e.name === branch)?.tree);

  // Schema completion and inline errors for jobs files, fetched from the server
  // the first time one is opened rather than on every page load — it is a
  // language service and a worker, and most files here are not YAML.
  useEffect(() => {
    if (isJobsFile(path)) {
      void enableJobsSchema();
    }
  }, [path]);

  // A file that is not a jobs file has no jobs problems, and stale ones from the
  // last file would otherwise underline lines in this one.
  useEffect(() => setJobsProblems([]), [path]);

  // Opening a notebook starts its kernel, the way opening one in VS Code starts the
  // language server. Without this a session appears only on the first run, and
  // completion would be dead until then — which is the wrong way round, since the
  // reason to want completion is that you have not run anything yet. Failures are
  // not reported here: the status poll below is what tells the user, once.
  useEffect(() => {
    if (isNotebook && allows.run) {
      api.startSession(path).catch(() => undefined);
    }
  }, [path, branch, isNotebook, allows.run]);

  // What the editor has open, told to the kernel on a debounce. Language features
  // answer about documents the server holds, so this is what makes them work at all
  // — and it is authoritative, so a deleted cell stops contributing its symbols.
  useEffect(() => {
    if (!isNotebook || cells == null || !canRun || !allows.run) {
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
  }, [path, branch, isNotebook, cells, canRun, allows.run, reloadSession]);

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
      navigate(editPath(projectSlug(), 'mine', path));
      setNotice(`Copied ${path} onto your branch.`);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  /**
   * Save a copy at a path you pick, and go there.
   *
   * The bytes come back from the server rather than out of the cell state,
   * after a flush: it is the one copy that is right whichever tab you are on,
   * and it is the same file the next person to open it will read.
   */
  async function saveAs() {
    setError(null);
    setNotice(null);
    try {
      await flush();
      const to = await saveNotebookAs(await api.notebookContent(branch, path), path);
      if (to != null) {
        navigate(editPath(projectSlug(), 'mine', to, tab as NotebookView));
        setNotice(`Saved as ${to} on your branch.`);
      }
    } catch (e) {
      setError((e as Error).message);
    }
  }

  /** Rename, or move to another folder — one operation, and the same one. */
  async function move() {
    setError(null);
    setNotice(null);
    try {
      await flush();
      const to = await moveNotebookTo(path, path);
      if (to != null) {
        navigate(editPath(projectSlug(), 'mine', to, tab as NotebookView));
        setNotice(`Moved to ${to}.`);
      }
    } catch (e) {
      setError((e as Error).message);
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
        setUndoStack([]);
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
    if (editingText) {
      const text = source ?? '';
      const written = await api.saveNotebookContent(path, text, keepalive);
      setSavedSource(text);
      // The server's verdict on the bytes it just wrote. Null for anything that
      // is not a jobs file, which is why this clears rather than only sets.
      setJobsProblems(written.problems ?? []);
    } else {
      await api.saveNotebookCells(path, toApiCells(cells ?? []), keepalive);
      setSaved(cells ?? []);
    }
  }, [path, editingText, source, cells]);

  const { status: saveStatus, flush, retry } = useAutosave(
    // The buffer itself is the revision: every edit is a new value, which is what
    // restarts the debounce.
    editingText ? source : cells,
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
   * Production's copy, for the diff. It comes from the content GET, which reads
   * any branch — only writing is your own branch. A 404 means the file exists
   * nowhere but here, so the original side is empty and the whole thing reads as
   * added.
   *
   * Fetched on arriving at the view rather than in the click that goes there:
   * the view is a URL, so it is arrived at by reload and by the back button too.
   */
  useEffect(() => {
    if (tab !== 'diff') {
      return;
    }
    let live = true;
    setProd(null);
    api
      .notebookContent('prod', path)
      .then((text) => live && setProd(text))
      .catch(() => live && setProd(''));
    return () => {
      live = false;
    };
  }, [tab, path]);

  async function promote() {
    // Switching a schedule off is not the same act as shipping a change, and a
    // bare "Promote?" reads identically for both. Name each job and say when it
    // would next have fired, so the confirmation carries the consequence.
    const stopping = (promotion?.unscheduling ?? []).map((job) => {
      const next = job.nextRun
        ? `next ${new Date(job.nextRun).toLocaleString('en-GB', {
            timeZone: 'UTC', weekday: 'short', day: '2-digit', month: 'short',
            hour: '2-digit', minute: '2-digit', hour12: false,
          })} UTC`
        : 'no schedule';
      return `  • ${job.name}${job.cron ? ` (${job.cron})` : ''} — ${next}`;
    });
    const question = stopping.length > 0
      ? `Promote ${path} to production?\n\nThis stops ${
          stopping.length === 1 ? 'this schedule' : `these ${stopping.length} schedules`
        }:\n${stopping.join('\n')}`
      : `Promote ${path} to production?`;
    if (!confirm(question)) {
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

  /** Typing. Deliberately not undoable here — inside a cell, Ctrl+Z is Monaco's
   *  own text undo and belongs to it. */
  function update(index: number, change: (cell: EditorCell) => EditorCell) {
    setCells((current) => current?.map((cell, i) => (i === index ? change(cell) : cell)) ?? current);
  }

  /**
   * Every structural change goes through here — anything that adds, removes,
   * reorders or retypes a cell — so the history cannot be forgotten by whichever
   * call site is added next. A change that turns out to be a no-op (moving the
   * first cell up) records nothing, or Undo would do nothing and look broken.
   */
  function edit(change: (current: EditorCell[]) => EditorCell[]) {
    if (cells == null) {
      return;
    }
    const next = change(cells);
    if (next === cells) {
      return;
    }
    setUndoStack((stack) => pushUndo(stack, cells));
    setCells(next);
  }

  const canUndo = undoStack.length > 0;

  function undo() {
    const snapshot = undoStack[undoStack.length - 1];
    if (snapshot == null || cells == null) {
      return;
    }
    setCells(restoreCells(snapshot, cells));
    setUndoStack(undoStack.slice(0, -1));
  }

  function insertAt(index: number, kind: 'code' | 'markdown') {
    edit((current) => insertCell(current, index, emptyCell(kind)));
  }

  function copyCell(index: number) {
    const cell = cells?.[index];
    if (cell != null) {
      setClipboard(cell);
    }
  }

  /** One action, so one history entry: undoing a cut puts the cell back where
   *  it was, rather than needing two presses to undo one gesture. */
  function cutCell(index: number) {
    const cell = cells?.[index];
    if (cell == null) {
      return;
    }
    setClipboard(cell);
    const remaining = removeCell(cells!, index);
    edit(() => remaining);
    setActiveId(neighbourCell(remaining, index));
  }

  /** Pasted cells are copies with fresh ids — see `copyOfCell`. The new one
   *  becomes active so Focus Mode, which shows one cell at a time, lands on it. */
  function pasteCell(index: number) {
    if (clipboard == null) {
      return;
    }
    const cell = copyOfCell(clipboard);
    edit((current) => insertCell(current, index, cell));
    setActiveId(cell.id);
  }

  /**
   * A connect directive becomes its own cell above the one you opened the wizard
   * from, in that cell's language — a connection is a statement about the session,
   * not part of the query you were writing.
   */
  function insertConnection(index: number, directive: string) {
    const source = cells?.[index];
    edit((current) =>
      insertCell(current, index, {
        ...emptyCell('code'),
        tag: source?.tag ?? null,
        languageId: source?.languageId ?? null,
        source: directive,
      }),
    );
    setConnectFor(null);
    setNotice('Connection cell added. Run it to open the connection.');
  }

  const focusing = mode === 'focus' && tab === 'edit';
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
        onTab={async (next) => {
          // Write before moving, and while the view being left is still the one
          // selected: `saveNow` picks its endpoint from it, so flushing after
          // the navigation would push the new view's untouched copy over the
          // edits you just made in the old one.
          await flush();
          navigate(editPath(projectSlug(), branch, path, next as NotebookView));
        }}
        isNotebook={isNotebook}
        isJobsFile={jobsFile}
        canRun={canRun}
        running={running}
        session={session}
        mode={mode}
        onMode={chooseMode}
        onRunAll={() => run(0, 'all')}
        onRestart={restartKernel}
        canUndo={canUndo}
        onUndo={undo}
        saveStatus={saveStatus}
        busy={busy}
        onSave={retry}
        onPromote={promote}
        promotion={promotion}
        standing={standing}
        onPush={push}
        onUpdate={updateFromTest}
        branch={branch}
        fileEditable={fileEditable(path)}
        onCopyToMine={copyToMine}
        onSaveAs={saveAs}
        onMove={move}
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

      {tab === 'edit' &&
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

            {privateConnections.length > 0 && (
              <Alert variant="warning" className="mx-4 mb-3 w-auto">
                <AlertDescription>
                  This notebook uses {privateConnections.length === 1 ? 'the private connection' : 'private connections'}{' '}
                  <strong>{privateConnections.join(', ')}</strong>.{' '}
                  {privateConnections.length === 1 ? 'It resolves' : 'They resolve'} only for you, so this
                  notebook fails for everybody else and for every scheduled run — and promotion will
                  refuse it. Make {privateConnections.length === 1 ? 'it' : 'them'} shared, or point the
                  notebook at a shared connection.
                </AlertDescription>
              </Alert>
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
                connectionType={connectionType}
                layout={layout}
                onActivate={activate}
                onChange={(cellId, value) =>
                  setCells((current) =>
                    (current ?? []).map((c) => (c.id === cellId ? { ...c, source: value } : c)))}
                onLanguage={(cellId, value) =>
                  edit((current) => current.map(
                    (c) => (c.id === cellId ? setCellLanguage(c, value, languages) : c)))}
                onRun={(cellId) => run(cells.findIndex((c) => c.id === cellId), 'one')}
                onClearOutput={(cellId) => setCleared((current) => new Set(current).add(cellId))}
                onLayout={setLayout}
                onDelete={(cellId) => {
                  const index = cells.findIndex((c) => c.id === cellId);
                  const remaining = removeCell(cells, index);
                  edit(() => remaining);
                  setActiveId(neighbourCell(remaining, index));
                }}
                onMove={(from, to) => edit((current) => moveCell(current, from, to))}
                onInsert={(afterId, kind) => {
                  const at = afterId == null ? 0 : cells.findIndex((c) => c.id === afterId) + 1;
                  const cell = emptyCell(kind);
                  edit((current) => insertCell(current, at, cell));
                  setActiveId(cell.id);
                }}
                clipboard={clipboard != null}
                onCut={(cellId) => cutCell(cells.findIndex((c) => c.id === cellId))}
                onCopy={(cellId) => copyCell(cells.findIndex((c) => c.id === cellId))}
                onPaste={(cellId, where) => {
                  const index = cells.findIndex((c) => c.id === cellId);
                  pasteCell(where === 'above' ? index : index + 1);
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
                  connectionId={connectionId}
                  connectionType={connectionType}
                  run={runState[cell.id] ?? null}
                  canRun={canRun}
                  busy={running}
                  onChange={(value) => update(index, (c) => ({ ...c, source: value }))}
                  onLanguage={(value) => edit((current) => current.map(
                    (c, i) => (i === index ? setCellLanguage(c, value, languages) : c)))}
                  onMove={(to) => edit((current) => moveCell(current, index, to))}
                  onDelete={() => edit((current) => removeCell(current, index))}
                  clipboard={clipboard != null}
                  onCut={() => cutCell(index)}
                  onCopy={() => copyCell(index)}
                  onPaste={(where) => pasteCell(where === 'above' ? index : index + 1)}
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

      {tab === 'overview' && (
        source == null ? (
          <p className="px-4 text-base text-muted-foreground">Loading…</p>
        ) : (
          <JobsOverview
            text={source}
            onChange={setSource}
            readOnly={!canWrite}
            notebooks={branchNotebooks}
          />
        )
      )}

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
              path={path}
              problems={jobsProblems}
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
  value, language, onChange, resetKey, path, problems,
}: {
  value: string;
  language: string;
  onChange: (value: string) => void;
  /** Changes when the file was replaced under the editor rather than typed into. */
  resetKey: number;
  /** The file, so the model's URI ends in its real name — see `modelPath`. */
  path: string;
  /** What the server said about this file when it was last saved. */
  problems: ApiJobsProblem[];
}) {
  const handle = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const container = useFillEditor(
    language, value, onChange, !useCanWrite(), resetKey, undefined, handle, undefined, path);

  // The server's problems as markers. Its own owner string, so setting these
  // never clears markers somebody else put on the model — today nothing else
  // does, and the day a real YAML language service works here, both should show.
  // The server is the one that knows the things a schema cannot: a cron that is
  // not a schedule, two jobs sharing a name.
  useEffect(() => {
    const model = handle.current?.getModel();
    if (model == null) {
      return;
    }
    monaco.editor.setModelMarkers(model, 'clrkernel-jobs', problems.map((problem) => ({
      severity: monaco.MarkerSeverity.Error,
      message: problem.message,
      startLineNumber: problem.line,
      startColumn: problem.column,
      // To the end of the line: the server reports where a problem starts, and
      // guessing where it ends would underline the wrong half of it.
      endLineNumber: problem.line,
      endColumn: model.getLineMaxColumn(Math.min(problem.line, model.getLineCount())),
    })));
  }, [problems, value]);

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

/**
 * The id of the first connection a notebook names that this reader can see.
 *
 * Names are what a notebook carries — an id would be meaningless in a file that
 * travels between servers — so the lookup happens here, per reader. A name nobody
 * can see resolves to nothing and the cells simply complete against the kernel
 * alone, which is what they did before.
 */
async function resolveConnection(
  names: string[],
): Promise<{ id: string | null; providerType: string | null }> {
  const nothing = { id: null, providerType: null };
  if (names.length === 0) {
    return nothing;
  }
  try {
    const { connections } = await api.connections();
    for (const name of names) {
      const matches = connections.filter((c) => c.name.toLowerCase() === name.toLowerCase());
      if (matches.length === 0) {
        continue;
      }
      // Two different questions about the same connection. Schema completion
      // needs one this server can open, so it keeps the `queryable` filter. Which
      // dialect may run on it is a fact about the connection either way — a
      // notebook's Oracle connection is an Oracle connection whether or not this
      // server has the driver — so the type is taken from any match.
      return {
        id: matches.find((c) => c.queryable)?.id ?? null,
        providerType: matches[0].type ?? null,
      };
    }
  } catch {
    // Completion is a convenience; failing to find a connection is not an error
    // worth putting on screen.
  }
  return nothing;
}
