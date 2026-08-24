import {
  ChevronDown,
  ChevronRight,
  Columns3,
  Database,
  FileCode2,
  Folder,
  Plug,
  PlugZap,
  Table2,
  type LucideIcon,
} from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  api,
  type ApiConnection,
  type ApiMetadataNode,
  type ApiObjectDetail,
} from '../api';

/**
 * Where a node sits, as the four coordinates the metadata route takes. The key
 * is the address: two schemas called `dbo` in different databases are different
 * nodes, and a key built from the name alone would collapse them.
 */
interface Address {
  database?: string;
  schema?: string;
  object?: string;
  kind?: string;
}

interface Node extends Address {
  key: string;
  label: string;
  depth: number;
  /** What it is, which decides its icon and what expanding it asks for. */
  type: 'connection' | 'database' | 'schema' | 'group' | 'object' | 'detail';
  connection: ApiConnection;
  /** Only on `group` — which of Tables / Views / Programmability this is. */
  group?: string;
  leaf?: boolean;
}

const ICONS: Record<string, LucideIcon> = {
  connection: Plug,
  database: Database,
  schema: Folder,
  group: Folder,
  table: Table2,
  view: Table2,
  procedure: FileCode2,
  function: FileCode2,
};

/** Objects are filed under the three folders SSMS uses, and functions and
 *  procedures share one — which is what Programmability means. */
const GROUPS: { key: string; label: string; kinds: string[] }[] = [
  { key: 'tables', label: 'Tables', kinds: ['table'] },
  { key: 'views', label: 'Views', kinds: ['view'] },
  { key: 'programmability', label: 'Programmability', kinds: ['procedure', 'function'] },
];

export function ConnectionTree({
  connections, selected, filter, onFilter, onSelect, onQuery, onScript, onNew, onEdit,
}: {
  connections: ApiConnection[];
  selected: string | null;
  filter: string;
  onFilter: (value: string) => void;
  onSelect: (connection: ApiConnection) => void;
  /** Select Top N / a qualified name dropped into the editor. */
  onQuery: (connection: ApiConnection, sql: string) => void;
  onScript: (connection: ApiConnection, node: Node) => void;
  onNew: () => void;
  onEdit: (connection: ApiConnection) => void;
}) {
  // Everything loaded so far, by node key. Absent means "not opened yet";
  // present means opened, so an empty schema stays open showing nothing rather
  // than re-fetching every time it is clicked.
  const [children, setChildren] = useState<Record<string, ApiMetadataNode[]>>({});
  const [details, setDetails] = useState<Record<string, ApiObjectDetail>>({});
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<Set<string>>(new Set());
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function toggle(node: Node) {
    const next = new Set(open);
    if (next.has(node.key)) {
      next.delete(node.key);
      setOpen(next);
      return;
    }
    next.add(node.key);
    setOpen(next);
    if (children[node.key] != null || details[node.key] != null || node.type === 'group') {
      return; // already loaded, or a group whose contents came with its schema
    }
    await load(node);
  }

  async function load(node: Node) {
    setBusy((current) => new Set(current).add(node.key));
    try {
      const level =
        node.type === 'connection' ? 'databases'
        : node.type === 'database' ? 'schemas'
        : node.type === 'schema' ? 'objects'
        : 'detail';
      const reply = await api.connectionMetadata<{ nodes?: ApiMetadataNode[] } & ApiObjectDetail>(
        node.connection.id,
        { level, database: node.database, schema: node.schema, object: node.object },
      );
      if (!reply.supported) {
        setErrors((e) => ({ ...e, [node.key]: reply.reason ?? 'Not browsable.' }));
        return;
      }
      if (reply.error) {
        setErrors((e) => ({ ...e, [node.key]: reply.error! }));
        return;
      }
      setErrors((e) => ({ ...e, [node.key]: '' }));
      if (level === 'detail') {
        setDetails((d) => ({ ...d, [node.key]: reply.payload as ApiObjectDetail }));
      } else {
        setChildren((c) => ({ ...c, [node.key]: reply.payload?.nodes ?? [] }));
      }
    } catch (e) {
      setErrors((current) => ({ ...current, [node.key]: (e as Error).message }));
    } finally {
      setBusy((current) => {
        const next = new Set(current);
        next.delete(node.key);
        return next;
      });
    }
  }

  /** Drops the pooled sockets and everything this connection had loaded. */
  async function disconnect(node: Node) {
    setChildren((c) => dropUnder(c, node.key));
    setDetails((d) => dropUnder(d, node.key));
    setOpen((current) => {
      const next = new Set(current);
      for (const key of next) {
        if (key.startsWith(node.key)) {
          next.delete(key);
        }
      }
      return next;
    });
    await api.disconnectConnection(node.connection.id).catch(() => undefined);
  }

  /** Reload one node and everything under it — the explicit Refresh, because a
   *  cached tree that quietly went stale is worse than one you refresh. */
  async function refresh(node: Node) {
    setChildren((c) => dropUnder(c, node.key));
    setDetails((d) => dropUnder(d, node.key));
    await load(node);
  }

  const rows = flatten(connections, children, details, open, filter);

  return (
    <div className="connection-tree">
      <div className="connection-tree-header">
        <Input
          value={filter}
          onChange={(e) => onFilter(e.target.value)}
          placeholder="Filter…"
          aria-label="Filter the tree"
          className="h-7 text-sm"
        />
        <Button variant="outline" size="sm" className="h-7 shrink-0 px-2 text-sm" onClick={onNew}>
          + New
        </Button>
      </div>

      <div className="connection-tree-rows">
        {connections.length === 0 && (
          <p className="p-3 text-sm text-muted-subtle">
            No connections yet. A shared one is managed by a server admin; your own is
            visible only to you.
          </p>
        )}
        {rows.map((node) => {
          // A connection is "connected" exactly when we have its objects — one fact
          // rather than a status kept beside the thing it describes and free to
          // disagree with it.
          const connected = node.type === 'connection' && children[node.key] != null;
          const Icon = node.type === 'connection' && connected
            ? PlugZap
            : ICONS[node.type === 'object' ? node.kind ?? 'table' : node.type] ?? Columns3;
          const expandable = node.type !== 'detail' && !node.leaf;
          const isOpen = open.has(node.key);
          return (
            <div key={node.key}>
              <div
                className={`connection-tree-row${selected === node.connection.id && node.type === 'connection' ? ' is-selected' : ''}`}
                style={{ paddingLeft: 6 + node.depth * 14 }}
              >
                {expandable ? (
                  <button
                    className="connection-tree-chevron"
                    onClick={() => toggle(node)}
                    aria-label={isOpen ? `Collapse ${node.label}` : `Expand ${node.label}`}
                    aria-expanded={isOpen}
                  >
                    {isOpen
                      ? <ChevronDown className="size-3.5" aria-hidden="true" />
                      : <ChevronRight className="size-3.5" aria-hidden="true" />}
                  </button>
                ) : (
                  <span className="connection-tree-chevron" aria-hidden="true" />
                )}
                <Icon
                  className={`size-3.5 shrink-0 ${connected ? 'text-status-success' : 'text-muted-subtle'}`}
                  aria-hidden="true"
                />
                <button
                  className="connection-tree-label"
                  onClick={() => {
                    if (node.type === 'connection') {
                      onSelect(node.connection);
                    } else if (node.type === 'object') {
                      onQuery(node.connection, selectTop(node));
                    } else {
                      void toggle(node);
                    }
                  }}
                  title={node.type === 'object' ? 'Select the first 1000 rows' : node.label}
                >
                  {node.label}
                </button>
                {node.type === 'connection' && (
                  <>
                    {connected && (
                      <span className="sr-only">connected</span>
                    )}
                    <ConnectionActions
                      node={node}
                      connected={connected}
                      onEdit={onEdit}
                      onRefresh={() => refresh(node)}
                      onConnect={() => toggle(node)}
                      onDisconnect={() => disconnect(node)}
                    />
                  </>
                )}
                {node.type === 'object' && (
                  <span className="connection-tree-actions">
                    <button onClick={() => copyName(node)} title="Copy the qualified name">⧉</button>
                    <button onClick={() => onScript(node.connection, node)} title="Script this object">
                      {'</>'}
                    </button>
                  </span>
                )}
                {busy.has(node.key) && <span className="connection-tree-busy">…</span>}
              </div>
              {errors[node.key] && (
                <p className="connection-tree-error" style={{ paddingLeft: 20 + node.depth * 14 }}>
                  {errors[node.key]}
                </p>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function ConnectionActions({
  node, connected, onEdit, onRefresh, onConnect, onDisconnect,
}: {
  node: Node;
  connected: boolean;
  onEdit: (connection: ApiConnection) => void;
  onRefresh: () => void;
  onConnect: () => void;
  onDisconnect: () => void;
}) {
  return (
    <span className="connection-tree-actions">
      {connected ? (
        <button onClick={onDisconnect} title="Disconnect — closes the pooled sockets">⏻</button>
      ) : (
        <button onClick={onConnect} title="Connect and list the databases">⏵</button>
      )}
      {connected && (
        <button onClick={onRefresh} title="Reload this connection's objects">⟳</button>
      )}
      {node.connection.canEdit && (
        <button onClick={() => onEdit(node.connection)} title="Edit this connection">✎</button>
      )}
    </span>
  );
}

/** `[db].[schema].[object]` — what you paste into a query somewhere else. */
function qualified(node: Node): string {
  return [node.database, node.schema, node.object]
    .filter(Boolean).map((part) => `[${part}]`).join('.');
}

function selectTop(node: Node): string {
  return node.kind === 'table' || node.kind === 'view'
    ? `SELECT TOP 1000 *\nFROM ${qualified(node)};`
    : `EXEC ${qualified(node)};`;
}

function copyName(node: Node) {
  void navigator.clipboard.writeText(qualified(node));
}

/** Drops a subtree's cached answers so Refresh actually re-asks. */
function dropUnder<T>(cache: Record<string, T>, key: string): Record<string, T> {
  const next: Record<string, T> = {};
  for (const [k, value] of Object.entries(cache)) {
    if (!k.startsWith(key)) {
      next[k] = value;
    }
  }
  return next;
}

/**
 * The tree as a flat list of rows, so a row's hover band spans the sidebar
 * whatever its depth — the same shape the notebook explorer uses.
 *
 * The filter narrows what has been *loaded*, which is the honest thing it can
 * do: the server has not been asked about the schemas nobody has opened, and a
 * filter that silently searched the whole database would be a different feature
 * with a very different cost.
 */
function flatten(
  connections: ApiConnection[],
  children: Record<string, ApiMetadataNode[]>,
  details: Record<string, ApiObjectDetail>,
  open: Set<string>,
  filter: string,
): Node[] {
  const needle = filter.trim().toLowerCase();
  const matches = (label: string) => needle === '' || label.toLowerCase().includes(needle);
  const rows: Node[] = [];

  for (const scope of ['shared', 'private'] as const) {
    const inScope = connections.filter((c) => c.scope === scope);
    if (inScope.length === 0) {
      continue;
    }
    rows.push({
      key: `scope:${scope}`, label: scope === 'shared' ? 'Shared' : 'Mine', depth: 0,
      type: 'group', connection: inScope[0], leaf: true,
    });
    for (const connection of inScope) {
      const key = `c:${connection.id}`;
      if (!matches(connection.name) && !open.has(key)) {
        continue;
      }
      rows.push({ key, label: connection.name, depth: 1, type: 'connection', connection });
      if (!open.has(key)) {
        continue;
      }
      for (const database of children[key] ?? []) {
        const dbKey = `${key}/d:${database.name}`;
        rows.push({
          key: dbKey, label: database.name, depth: 2, type: 'database', connection,
          database: database.name,
        });
        if (!open.has(dbKey)) {
          continue;
        }
        for (const schema of children[dbKey] ?? []) {
          const schemaKey = `${dbKey}/s:${schema.name}`;
          rows.push({
            key: schemaKey, label: schema.name, depth: 3, type: 'schema', connection,
            database: database.name, schema: schema.name,
          });
          if (!open.has(schemaKey)) {
            continue;
          }
          const objects = children[schemaKey] ?? [];
          for (const group of GROUPS) {
            const groupKey = `${schemaKey}/g:${group.key}`;
            const inGroup = objects.filter(
              (o) => group.kinds.includes(o.kind) && matches(o.name));
            if (inGroup.length === 0) {
              continue; // a folder that opens onto nothing is not shown
            }
            rows.push({
              key: groupKey, label: `${group.label} (${inGroup.length})`, depth: 4,
              type: 'group', group: group.key, connection,
              database: database.name, schema: schema.name,
            });
            if (!open.has(groupKey)) {
              continue;
            }
            for (const object of inGroup) {
              const objectKey = `${groupKey}/o:${object.name}`;
              rows.push({
                key: objectKey, label: object.name, depth: 5, type: 'object', connection,
                database: database.name, schema: schema.name, object: object.name, kind: object.kind,
              });
              if (!open.has(objectKey)) {
                continue;
              }
              const detail = details[objectKey];
              for (const column of detail?.columns ?? []) {
                rows.push({
                  key: `${objectKey}/c:${column.name}`, depth: 6, type: 'detail', connection,
                  leaf: true,
                  label: `${column.primaryKey ? '🔑 ' : ''}${column.name} : ${column.type}`
                    + `${column.nullable ? ' null' : ''}`,
                });
              }
              for (const index of detail?.indexes ?? []) {
                rows.push({
                  key: `${objectKey}/i:${index}`, label: index, depth: 6, type: 'detail',
                  connection, leaf: true,
                });
              }
            }
          }
        }
      }
    }
  }
  return rows;
}
