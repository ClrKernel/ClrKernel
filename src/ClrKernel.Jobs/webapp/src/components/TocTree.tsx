import type { CellRunState } from '../notebook';
import type { TocNode } from '../toc';

/**
 * The notebook's contents, as a tree. Sections come from markdown headings;
 * every cell is a leaf under the nearest one above it.
 *
 * Clicking a section chevron expands or collapses only — it never changes which
 * cell you are editing. Clicking the section's own row selects the heading cell,
 * because a heading is a cell too and has to be editable from here.
 */
export function TocTree({
  nodes, activeId, collapsed, runState, onActivate, onToggle, onKeyDown,
}: {
  nodes: TocNode[];
  activeId: string | null;
  collapsed: ReadonlySet<string>;
  runState: Record<string, CellRunState>;
  onActivate: (cellId: string) => void;
  onToggle: (sectionId: string) => void;
  onKeyDown: (event: React.KeyboardEvent) => void;
}) {
  return (
    <ul className="focus-toc" role="tree" aria-label="Notebook contents" onKeyDown={onKeyDown}>
      {nodes.map((node) => (
        <TocNodeRow
          key={node.kind === 'leaf' ? node.cellId : `s-${node.id}`}
          node={node}
          activeId={activeId}
          collapsed={collapsed}
          runState={runState}
          onActivate={onActivate}
          onToggle={onToggle}
        />
      ))}
    </ul>
  );
}

function TocNodeRow({
  node, activeId, collapsed, runState, onActivate, onToggle,
}: {
  node: TocNode;
  activeId: string | null;
  collapsed: ReadonlySet<string>;
  runState: Record<string, CellRunState>;
  onActivate: (cellId: string) => void;
  onToggle: (sectionId: string) => void;
}) {
  if (node.kind === 'leaf') {
    const run = runState[node.cellId] ?? null;
    const active = node.cellId === activeId;
    return (
      <li role="none">
        <div
          role="treeitem"
          aria-selected={active}
          // Only the active row is tabbable, so Tab reaches the tree once and
          // the arrow keys take over inside it.
          tabIndex={active ? 0 : -1}
          data-cell={node.cellId}
          className={`focus-toc-leaf${active ? ' focus-toc-active' : ''}`}
          title={node.title || node.label}
          onClick={() => onActivate(node.cellId)}
        >
          <StatusDot run={run} kind={node.cell.kind} />
          <span className="focus-toc-count">
            {node.cell.kind === 'markdown' ? '' : `[${run?.executionCount ?? ' '}]`}
          </span>
          <span className="focus-toc-label">{node.label}</span>
        </div>
      </li>
    );
  }

  const open = !collapsed.has(node.id);
  return (
    <li role="none">
      <div className="focus-toc-section" role="treeitem" aria-expanded={open} tabIndex={-1}>
        <button
          type="button"
          className="focus-toc-chevron"
          aria-label={open ? `Collapse ${node.label}` : `Expand ${node.label}`}
          onClick={() => onToggle(node.id)}
        >
          {open ? '▾' : '▸'}
        </button>
        <span className="focus-toc-section-label" title={node.label}>{node.label}</span>
      </div>
      {open && (
        <ul role="group" className="focus-toc-children">
          {node.children.map((child) => (
            <TocNodeRow
              key={child.kind === 'leaf' ? child.cellId : `s-${child.id}`}
              node={child}
              activeId={activeId}
              collapsed={collapsed}
              runState={runState}
              onActivate={onActivate}
              onToggle={onToggle}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

/**
 * What a cell did, at a glance. An errored cell has to be findable from here
 * without opening every cell in turn — that is most of why the tree carries
 * status at all.
 */
function StatusDot({ run, kind }: { run: CellRunState | null; kind: 'code' | 'markdown' }) {
  if (kind === 'markdown') {
    return <span className="focus-toc-dot focus-toc-dot-md" aria-hidden="true">¶</span>;
  }
  const status = run?.status ?? 'idle';
  const title =
    status === 'running' ? 'Running' :
    status === 'failed' ? 'Failed' :
    status === 'succeeded' ? 'Succeeded' :
    status === 'skipped' ? 'Skipped' :
    status === 'pending' ? 'Queued' : 'Not run';
  return (
    <span className={`focus-toc-dot focus-toc-dot-${status}`} title={title} role="img" aria-label={title}>
      {status === 'running' ? '◐' : status === 'failed' ? '✕' : status === 'succeeded' ? '●' : '○'}
    </span>
  );
}
