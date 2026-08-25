import { useCallback, useEffect, useRef, useState } from 'react';
import type * as monaco from 'monaco-editor';
import { useNavigate, useParams } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { api, type ApiConnection, type ApiQueryResult } from '../api';
import { ConnectionForm } from '../components/ConnectionForm';
import { ConnectionTree } from '../components/ConnectionTree';
import { ErrorBanner } from '../components/common';
import { ResultGrid } from '../components/ResultGrid';
import { Splitter } from '../components/Splitter';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useFillEditor } from '../monaco/useMonaco';
import { connectionsPath } from '../routes';

/** Where the divider sits, as a fraction of the work area's height. */
const DEFAULT_SPLIT = 0.45;

/** How wide the tree starts. Object names are long, so it is wider than a file tree. */
const DEFAULT_TREE = 280;

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
  const [split, setSplit] = useState(DEFAULT_SPLIT);
  const [treeWidth, setTreeWidth] = useState(DEFAULT_TREE);
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

  // A ref, because the editor binds its keys once on creation and would
  // otherwise be calling the first render's `run` for the life of the page.
  const latestRun = useRef(run);
  latestRun.current = run;
  const editor = useFillEditor(
    'sql', sql, setSql, false, resetKey, (text) => void latestRun.current(text), editorHandle);

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
      navigate(connectionsPath(connection.id));
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

  function onTreeDrag(x: number) {
    const box = page.current?.getBoundingClientRect();
    if (box) {
      setTreeWidth(Math.min(640, Math.max(180, x - box.left)));
    }
  }

  function onSplitDrag(y: number) {
    const box = work.current?.getBoundingClientRect();
    if (box) {
      setSplit(Math.min(0.85, Math.max(0.15, (y - box.top) / box.height)));
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
        onReset={() => setTreeWidth(DEFAULT_TREE)}
      />

      <div className="connections-work" ref={work}>
        <ErrorBanner error={error} />

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
          onReset={() => setSplit(DEFAULT_SPLIT)}
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
