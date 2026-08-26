import { useCallback, useEffect, useRef, useState } from 'react';
import type * as monaco from 'monaco-editor';
import { useNavigate, useParams } from 'react-router-dom';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  api,
  projectSlug,
  type ApiConnection,
  type ApiQueryAudit,
  type ApiQueryResult,
  type ApiSavedQuery,
} from '../api';
import { editPath } from '../routes';
import { SavePlus } from 'lucide-react';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { STATUS_LABEL, STATUS_TITLE } from '../autosave';
import { moveNotebookTo, saveNotebookAs } from '../newNotebook';
import { pendingInsert, scratchNotebook, scratchPath, sqlOf, suggestedName } from '../scratch';
import { useAutosave } from '../useAutosave';
import { ConnectionForm } from '../components/ConnectionForm';
import { ConnectionTree } from '../components/ConnectionTree';
import { ErrorBanner, usePolling } from '../components/common';
import { ResultGrid } from '../components/ResultGrid';
import { Splitter } from '../components/Splitter';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useFillEditor } from '../monaco/useMonaco';
import { clamp, loadLayout, saveLayout, DEFAULT_LAYOUT, MAX_TREE, MIN_TREE } from '../prefs';
import { connectionsPath } from '../routes';
import { useIsProjectMember, useIsServerAdmin } from '../sessionContext';


/**
 * The Connections area: saved connections and their objects on the left, a query
 * editor above its results on the right.
 *
 * The split is the same component Focus Mode drags, for the same reason it was
 * worth having one: two implementations of a resizable pane means two places to
 * fix the Monaco relayout that a drag needs.
 */
export function Connections() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [connections, setConnections] = useState<ApiConnection[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<ApiConnection | null>(null);
  const [creating, setCreating] = useState(false);
  // How the workspace is shaped is remembered, the way the notebook editor's panes
  // are: you dragged it there once.
  const [layout, setLayout] = useState(loadLayout);
  const split = layout.connectionsSplit;
  const treeWidth = layout.connectionsTreeWidth;
  const page = useRef<HTMLDivElement | null>(null);
  const work = useRef<HTMLDivElement | null>(null);
  const editorHandle = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);

  const [sql, setSql] = useState('');
  const [resetKey, setResetKey] = useState(0);
  const [result, setResult] = useState<ApiQueryResult | null>(null);
  const [tab, setTab] = useState('r0');
  const [running, setRunning] = useState<string | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const [password, setPassword] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  // What is written to the scratch file, so "have you typed since" is a
  // comparison rather than a flag that has to be cleared in every path.
  const [savedSql, setSavedSql] = useState('');
  const [history, setHistory] = useState(false);
  const [savedOpen, setSavedOpen] = useState(false);

  const selected = connections.find((c) => c.id === id) ?? null;

  useEffect(() => {
    api.connections()
      .then((reply) => setConnections(reply.connections))
      .catch((e) => setError((e as Error).message));
  }, []);

  // The clock while a query runs. A query with no elapsed time on screen is
  // indistinguishable from one that has quietly died.
  useEffect(() => {
    if (running == null) {
      return;
    }
    const started = Date.now();
    const timer = window.setInterval(() => setElapsed(Date.now() - started), 100);
    return () => window.clearInterval(timer);
  }, [running]);

  const run = useCallback(async (text: string) => {
    if (selected == null || running != null || text.trim().length === 0) {
      return;
    }
    // The id is ours, not the server's: Cancel has to be able to name the query
    // before the response it would otherwise learn the id from has arrived.
    const queryId = crypto.randomUUID();
    setRunning(queryId);
    setElapsed(0);
    setError(null);
    try {
      const reply = await api.runQuery(selected.id, text, queryId, password || undefined);
      setResult(reply);
      setTab(reply.resultSets.length > 0 ? 'r0' : 'messages');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRunning(null);
    }
  }, [selected, running, password]);

  /**
   * The query editor's buffer is a notebook on your branch, one per connection.
   *
   * `owner` is which connection the text in the editor belongs to, and it moves
   * only when a load completes — never when the selection changes. That is the
   * whole trick: between clicking another connection and its file arriving,
   * `sql` is still the old query and `owner` still names the old file, so the
   * flush that happens on the way past writes the right bytes to the right path
   * instead of stamping them over the connection you just picked.
   */
  const owner = useRef<{ id: string; name: string; path: string } | null>(null);
  /** A script scheduled for a connection whose file has not loaded yet. */
  const pending = useRef<string | null>(null);
  const mayEdit = useIsProjectMember();
  const { data: health } = usePolling(() => api.health(), null);
  // No git workflow means no branch to write to. The page still works — it just
  // holds the query in memory, the way it always used to.
  const backed = (health?.gitEnabled ?? false) && mayEdit;

  const writeScratch = useCallback(async (keepalive = false) => {
    const who = owner.current;
    if (who == null) {
      return;
    }
    const text = sql;
    await api.saveNotebookContent(who.path, scratchNotebook(who.name, text), keepalive);
    setSavedSql(text);
  }, [sql]);

  const dirty = backed && owner.current != null && sql !== savedSql;
  const { status: saveStatus, flush } = useAutosave(sql, dirty, writeScratch);

  useEffect(() => {
    if (selected == null || !backed) {
      return;
    }
    let live = true;
    void (async () => {
      // Before the buffer is replaced, not after: `flush` writes through
      // `owner`, which still names the connection this text came from.
      await flush();
      const path = scratchPath(selected.id);
      const text = await api.notebookContent('mine', path).then(sqlOf).catch(() => '');
      if (!live) {
        return;
      }
      const carried = pending.current;
      pending.current = null;
      owner.current = { id: selected.id, name: selected.name, path };
      // `savedSql` is what is on disk, so a carried script leaves the buffer
      // dirty and autosaves itself a moment later.
      setSavedSql(text);
      setSql(pendingInsert(text, carried));
      setResetKey((key) => key + 1);
    })();
    return () => {
      live = false;
    };
    // Deliberately only the id and whether there is anywhere to write: a rename
    // of the connection must not throw away what you are in the middle of.
  }, [selected?.id, backed]);

  // A ref, because the editor binds its keys once on creation and would
  // otherwise be calling the first render's `run` for the life of the page.
  const latestRun = useRef(run);
  latestRun.current = run;
  // A ref, so the editor — created once — always asks for the connection that is
  // selected now rather than the one that was selected when it was built.
  const selectedId = useRef<string | null>(null);
  selectedId.current = selected?.id ?? null;
  const editor = useFillEditor(
    'sql', sql, setSql, false, resetKey, (text) => void latestRun.current(text), editorHandle,
    () => selectedId.current);

  async function cancel() {
    if (selected == null || running == null) {
      return;
    }
    await api.cancelQuery(selected.id, running).catch(() => undefined);
  }

  /**
   * Puts generated SQL into the editor **at the cursor**, never over what is there.
   *
   * It replaced the buffer at first, which meant picking "Select Top 1000" threw
   * away whatever you were part-way through writing. Inserting is the same thing
   * when the editor is empty and costs nothing when it is not — and it is what
   * every editor's snippet insertion does, so it needs no explaining.
   */
  function into(connection: ApiConnection, text: string) {
    if (connection.id !== id) {
      // Scripting an object under another connection navigates, and the load
      // that follows replaces the buffer. Inserting here would put the script
      // into *this* connection's file on the flush on the way past, and then
      // wipe it off the screen — so it travels as a value and is applied on the
      // far side. Only when there is a file to travel to: with no branch to
      // write to nothing reloads, and inserting is still right.
      if (backed) {
        pending.current = text;
      }
      navigate(connectionsPath(connection.id));
      if (backed) {
        return;
      }
    }
    const live = editorHandle.current;
    if (live == null) {
      setSql(text);
      setResetKey((key) => key + 1);
      return;
    }
    const selection = live.getSelection();
    const body = live.getValue().trim().length === 0 ? text : `\n${text}`;
    live.executeEdits('connections', [{
      range: selection ?? live.getModel()!.getFullModelRange(),
      text: body,
      forceMoveMarkers: true,
    }]);
    live.focus();
    setSql(live.getValue());
  }

  function reshape(change: Partial<typeof layout>) {
    setLayout((current) => {
      const next = { ...current, ...change };
      saveLayout(next);
      return next;
    });
  }

  /**
   * Puts a whole saved query in the editor, replacing what is there.
   *
   * Not inserted at the cursor like the snippets are: "open" means this query, and
   * dropping it into the middle of a half-written one produces a mangled hybrid of
   * both. Replacing can lose work, so it asks first when there is any — the one
   * case where a confirm earns its interruption.
   */
  function replaceEditor(text: string) {
    const live = editorHandle.current;
    if (live != null && live.getValue().trim().length > 0
      && !confirm('Replace what is in the editor?')) {
      return;
    }
    setSql(text);
    setResetKey((key) => key + 1);
    live?.focus();
  }

  function onTreeDrag(x: number) {
    const box = page.current?.getBoundingClientRect();
    if (box) {
      reshape({ connectionsTreeWidth: clamp(x - box.left, MIN_TREE, MAX_TREE) });
    }
  }

  /**
   * Save a copy of the scratch as a notebook, and stay here.
   *
   * Staying is the difference between this and Move. You keep a copy because the
   * query is worth keeping; you are not finished with the connection, and being
   * thrown into the notebook editor mid-thought would be the wrong answer to
   * "keep this".
   */
  async function saveAs() {
    const who = owner.current;
    if (who == null) {
      return;
    }
    setError(null);
    setNotice(null);
    try {
      await flush();
      const to = await saveNotebookAs(scratchNotebook(who.name, sql), suggestedName(who.name));
      if (to != null) {
        setNotice(`Saved as ${to} on your branch.`);
      }
    } catch (e) {
      setError((e as Error).message);
    }
  }

  /**
   * Move the scratch into your notebooks, and go there.
   *
   * The other half of the pair, and the one that ends the scratch: the query
   * stops being a thing this page is holding for you and becomes a file with a
   * name. Nothing is left behind here, so the editor empties on the way out.
   */
  async function moveOut() {
    const who = owner.current;
    if (who == null) {
      return;
    }
    setError(null);
    setNotice(null);
    try {
      await flush();
      const to = await moveNotebookTo(who.path, suggestedName(who.name));
      if (to != null) {
        setSavedSql('');
        setSql('');
        setResetKey((key) => key + 1);
        navigate(editPath(projectSlug(), 'mine', to));
      }
    } catch (e) {
      setError((e as Error).message);
    }
  }

  function onSplitDrag(y: number) {
    const box = work.current?.getBoundingClientRect();
    if (box) {
      reshape({ connectionsSplit: clamp((y - box.top) / box.height, 0.15, 0.85) });
    }
  }

  function saved(connection: ApiConnection) {
    setConnections((current) => {
      const rest = current.filter((c) => c.id !== connection.id);
      return [...rest, connection].sort((a, b) =>
        a.scope === b.scope ? a.name.localeCompare(b.name) : a.scope.localeCompare(b.scope));
    });
    setEditing(null);
    setCreating(false);
    navigate(connectionsPath(connection.id));
  }

  const tabs = [
    ...(result?.resultSets ?? []).map((_, i) => ({
      id: `r${i}`,
      label: (result?.resultSets.length ?? 0) > 1 ? `Result ${i + 1}` : 'Results',
    })),
    { id: 'messages', label: `Messages${result?.error ? ' (1)' : ''}` },
  ];

  return (
    <div className="connections-page" ref={page}>
      <div className="connection-tree-pane" style={{ width: treeWidth }}>
        <ConnectionTree
          connections={connections}
          selected={id ?? null}
          filter={filter}
          onFilter={setFilter}
          onSelect={(c) => navigate(connectionsPath(c.id))}
          onQuery={into}
          onScript={async (connection, node, variant) => {
            const reply = await api.connectionMetadata<{ script: string }>(connection.id, {
              level: 'script',
              database: node.database,
              schema: node.schema,
              object: node.object,
              kind: node.kind,
              variant,
            });
            into(connection, reply.payload?.script ?? reply.error ?? '');
          }}
          onNew={() => setCreating(true)}
          onEdit={setEditing}
        />
      </div>

      <Splitter
        orientation="vertical"
        label="Connections width"
        onDrag={onTreeDrag}
        onReset={() => reshape({ connectionsTreeWidth: DEFAULT_LAYOUT.connectionsTreeWidth })}
      />

      <div className="connections-work" ref={work}>
        <ErrorBanner error={error} />
        {notice && (
          <Alert variant="success" className="mx-3 mt-3 w-auto">
            <AlertDescription className="text-status-success">{notice}</AlertDescription>
          </Alert>
        )}

        <div className="connections-toolbar">
          {selected == null ? (
            <span className="text-sm text-muted-subtle">Pick a connection to query it.</span>
          ) : (
            <>
              <strong>{selected.name}</strong>
              <Badge variant="outline" className="font-normal">
                {selected.scope === 'shared' ? 'shared' : 'mine'}
              </Badge>
              {!selected.canExecute && (
                <span className="text-sm text-muted-subtle">{selected.canExecuteReason}</span>
              )}
            </>
          )}
          <span className="spacer" />
          {selected?.promptForPassword && (
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder="password for this session"
              aria-label="Password for this session"
              className="h-7 rounded-lg border border-input bg-background px-2 text-sm"
            />
          )}
          {selected != null && (
            <Button
              variant="outline"
              size="sm"
              className="h-7 px-2 text-sm"
              onClick={() => setHistory(true)}
              title="What has been run — here, or by you anywhere"
            >
              History
            </Button>
          )}
          <Button
            variant="outline"
            size="sm"
            className="h-7 px-2 text-sm"
            onClick={() => setSavedOpen(true)}
            title="Queries you and your colleagues have kept"
          >
            Saved
          </Button>
          {backed && (
            <span
              className="whitespace-nowrap text-sm text-muted-subtle"
              title={STATUS_TITLE[saveStatus]}
            >
              {STATUS_LABEL[saveStatus]}
            </span>
          )}
          {/* The same menu, in the same place, as the notebook editor's: this is
              a one-cell notebook, and the two things you do to its name are the
              two things you do to any other notebook's. */}
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="outline"
                size="sm"
                className="h-7 px-2 text-sm"
                disabled={!backed || selected == null || sql.trim().length === 0}
                aria-label="File"
                title="Keep this query as a notebook on your branch"
              >
                <SavePlus className="size-3.5" aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={() => void saveAs()}>Save a copy as…</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => void moveOut()}>Move to my notebooks…</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          {running != null ? (
            <>
              <span className="text-sm text-muted-subtle">{(elapsed / 1000).toFixed(1)}s</span>
              <Button variant="outline" size="sm" className="h-7 px-2 text-sm" onClick={cancel}>
                Cancel
              </Button>
            </>
          ) : (
            <Button
              size="sm"
              className="h-7 px-2 text-sm"
              disabled={selected == null || !selected.canExecute}
              onClick={() => void run(sql)}
              title="Ctrl+Enter or F5 — runs the selection when there is one"
            >
              ▶ Run
            </Button>
          )}
        </div>

        {/* Rendered whether or not a connection is picked, and deliberately: the
            editor is created once when its container mounts, so a pane that only
            appears after a selection would be a pane Monaco was never told about.
            It also keeps the layout from jumping when you click the first one. */}
        <div className="connections-editor" style={{ height: `${split * 100}%` }} ref={editor} />

        <Splitter
          orientation="horizontal"
          label="Query and results"
          onDrag={onSplitDrag}
          onReset={() => reshape({ connectionsSplit: DEFAULT_LAYOUT.connectionsSplit })}
        />

        <div className="connections-results">
          {result == null ? (
            <p className="p-3 text-sm text-muted-subtle">
              Ctrl+Enter runs the query. With a selection, it runs just that.
            </p>
          ) : (
            <>
              {/* Component state, not routes: a URL naming "Result 3" of a query
                  you no longer have on screen would mean nothing. */}
              <Tabs value={tab} onValueChange={setTab}>
                <TabsList>
                  {tabs.map((t) => (
                    <TabsTrigger key={t.id} value={t.id}>{t.label}</TabsTrigger>
                  ))}
                </TabsList>
              </Tabs>
              {tab === 'messages' ? (
                <div className="connections-messages">
                  {result.error && <p className="text-destructive">{result.error}</p>}
                  {result.messages.map((message, i) => <p key={i}>{message}</p>)}
                  <p className="text-muted-subtle">
                    {result.elapsedMs.toFixed(0)} ms{result.canceled ? ' · cancelled' : ''}
                  </p>
                </div>
              ) : (
                result.resultSets[Number(tab.slice(1))] != null && (
                  <ResultGrid set={result.resultSets[Number(tab.slice(1))]} />
                )
              )}
            </>
          )}
        </div>
      </div>

      {history && selected != null && (
        <HistoryPanel
          connection={selected}
          onUse={(statement) => {
            setHistory(false);
            into(selected, statement);
          }}
          onClose={() => setHistory(false)}
        />
      )}

      {savedOpen && (
        <SavedQueriesPanel
          connection={selected}
          sql={sql}
          onOpen={(query) => {
            setSavedOpen(false);
            replaceEditor(query.sql);
          }}
          onClose={() => setSavedOpen(false)}
        />
      )}

      {(creating || editing != null) && (
        <ConnectionForm
          connection={editing}
          onSaved={saved}
          onClose={() => {
            setCreating(false);
            setEditing(null);
          }}
          onDeleted={(removed) => {
            setConnections((current) => current.filter((c) => c.id !== removed));
            setEditing(null);
            navigate(connectionsPath());
          }}
        />
      )}
    </div>
  );
}

/**
 * What has been run against a shared connection: who, when, and what they sent.
 *
 * The same question the manual-run audit answers about notebooks, in a different
 * costume — so it shows the same things and, like that one, shows an admin
 * everybody's and everyone else only their own. That filtering is the server's;
 * this renders what it is given.
 */
function HistoryPanel({ connection, onUse, onClose }: {
  connection: ApiConnection;
  onUse: (statement: string) => void;
  onClose: () => void;
}) {
  /**
   * Two genuinely different questions, which is why they are a toggle rather than
   * one list. *This connection* is an audit of a database — on a shared one a
   * server admin sees everybody's. *Mine* is your own record of your own work,
   * across every connection including your own private ones, and nobody else ever
   * sees it.
   */
  const [scope, setScope] = useState<'connection' | 'mine'>('connection');
  const [rows, setRows] = useState<ApiQueryAudit[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setRows(null);
    const asking = scope === 'connection' ? api.connectionHistory(connection.id) : api.queryHistory();
    asking
      .then((result) => live && setRows(result.history))
      .catch((e) => live && setError((e as Error).message));
    return () => {
      live = false;
    };
  }, [connection.id, scope]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal modal-wide" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4">
          <h2 style={{ margin: 0 }}>History</h2>
          <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={onClose}>✕</Button>
        </div>
        <Tabs value={scope} onValueChange={(v) => setScope(v as 'connection' | 'mine')}>
          <TabsList>
            <TabsTrigger value="connection">{connection.name}</TabsTrigger>
            <TabsTrigger value="mine">Everything I have run</TabsTrigger>
          </TabsList>
        </Tabs>
        <ErrorBanner error={error} />
        {rows == null && !error && (
          <p className="text-base text-muted-foreground">Reading the log…</p>
        )}
        {rows?.length === 0 && (
          <p className="text-base text-muted-foreground">
            {scope === 'connection'
              ? 'Nothing has been run against this connection yet.'
              : 'You have not run anything yet.'}
          </p>
        )}
        <div className="query-history">
          {(rows ?? []).map((row) => (
            <div key={row.id} className="query-history-row">
              <div className="query-history-meta">
                <strong>{scope === 'mine' ? row.connectionName : row.actorName ?? 'somebody'}</strong>
                <span>{new Date(row.startedAt).toLocaleString()}</span>
                <Badge variant="outline" className="font-normal">{row.outcome}</Badge>
                {row.leastPrivilege && (
                  <Badge variant="outline" className="font-normal">read-only login</Badge>
                )}
                <span>{Math.round(row.durationMs)} ms</span>
                <span className="spacer" />
                {/* Into the editor at the cursor, like everything else that writes
                    there — running somebody's old statement is a decision they take
                    after reading it, not a button that does it for them. */}
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                  onClick={() => onUse(row.statement)}>
                  Use
                </Button>
              </div>
              <pre>{row.statement}</pre>
              {row.errorSummary && (
                <p className="text-sm text-destructive">{row.errorSummary}</p>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

/**
 * Queries somebody kept — shared ones a server admin manages, and your own.
 *
 * The same two lists connections come in, and deliberately so: they are used
 * together, and a different rule for each would be a rule nobody could remember.
 */
function SavedQueriesPanel({ connection, sql, onOpen, onClose }: {
  /** Only to label what is being saved. Opening one needs no connection: a saved
   *  query is text, and which connection to run it on is the next decision. */
  connection: ApiConnection | null;
  sql: string;
  onOpen: (query: ApiSavedQuery) => void;
  onClose: () => void;
}) {
  const isAdmin = useIsServerAdmin();
  const [queries, setQueries] = useState<ApiSavedQuery[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [scope, setScope] = useState<'shared' | 'private'>('private');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.savedQueries()
      .then((result) => setQueries(result.queries))
      .catch((e) => setError((e as Error).message));
  }, []);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const created = await api.saveQuery({
        name,
        scope,
        sql,
        // A hint, not a requirement: the query outlives the connection it was
        // written against, and the server keeps it either way.
        connectionId: connection?.id ?? null,
        connectionName: connection?.name ?? null,
      });
      setQueries((current) => [...(current ?? []), created]);
      setName('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function remove(query: ApiSavedQuery) {
    if (!confirm(`Delete the saved query '${query.name}'?`)) {
      return;
    }
    try {
      await api.deleteSavedQuery(query.id);
      setQueries((current) => (current ?? []).filter((q) => q.id !== query.id));
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const groups: { scope: 'shared' | 'private'; label: string }[] = [
    { scope: 'shared', label: 'Shared' },
    { scope: 'private', label: 'Mine' },
  ];

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal modal-wide" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4">
          <h2 style={{ margin: 0 }}>Saved queries</h2>
          <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={onClose}>✕</Button>
        </div>
        <ErrorBanner error={error} />

        {sql.trim().length > 0 && (
          <div className="saved-query-new">
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Save what is in the editor as…"
              aria-label="Name for this query"
            />
            {isAdmin && (
              <select value={scope} onChange={(e) => setScope(e.target.value as 'shared' | 'private')}
                aria-label="Visible to">
                <option value="private">Only me</option>
                <option value="shared">Everyone</option>
              </select>
            )}
            <Button size="sm" disabled={busy || name.trim().length === 0} onClick={save}>
              Save
            </Button>
          </div>
        )}

        {queries == null && !error && (
          <p className="text-base text-muted-foreground">Looking…</p>
        )}
        {queries?.length === 0 && (
          <p className="text-base text-muted-foreground">
            Nothing saved yet. Write a query and keep it here to find it again.
          </p>
        )}

        {groups.map(({ scope: group, label }) => {
          const inGroup = (queries ?? []).filter((q) => q.scope === group);
          if (inGroup.length === 0) {
            return null;
          }
          return (
            <div key={group}>
              <h3>{label}</h3>
              <div className="query-history">
                {inGroup.map((query) => (
                  <div key={query.id} className="query-history-row">
                    <div className="query-history-meta">
                      <strong>{query.name}</strong>
                      {query.connectionName && <span>{query.connectionName}</span>}
                      <span>{query.createdByName}</span>
                      <span className="spacer" />
                      <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                        title="Put it in the editor" onClick={() => onOpen(query)}>
                        Open
                      </Button>
                      {query.canEdit && (
                        <Button variant="outline" size="sm"
                          className="h-6 px-2 text-sm text-destructive hover:bg-destructive/10 hover:text-destructive"
                          onClick={() => void remove(query)}>
                          Delete
                        </Button>
                      )}
                    </div>
                    <pre>{query.sql}</pre>
                  </div>
                ))}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
