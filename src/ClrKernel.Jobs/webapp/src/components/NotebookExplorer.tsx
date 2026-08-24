import { ChevronLeft, ChevronRight, FilePlus2, GitBranch } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { api, projectSlug, type TreeNode } from '../api';
import { createNotebook, promptForNotebook } from '../newNotebook';
import { saveBranch } from '../prefs';
import { useIsProjectMember } from '../sessionContext';
import { usePolling } from './common';

/** The editor link for one file on one branch. */
function editHref(path: string, branch: string): string {
  return `/edit?project=${encodeURIComponent(projectSlug())}`
    + `&path=${encodeURIComponent(path)}`
    + `&branch=${encodeURIComponent(branch)}`;
}

/** Flattened tree row — the render is a flat list so indentation is padding. */
interface Row {
  key: string;
  name: string;
  depth: number;
  /** Set on files that can be opened; folders and jobs files leave it null. */
  path: string | null;
  kind: 'folder' | 'notebook' | 'jobs';
  open?: boolean;
}

/**
 * Depth-first, skipping the children of collapsed folders. A flat list rather
 * than nested components so a row's hover and selection band can span the full
 * sidebar width regardless of how deep the path is.
 */
function flatten(nodes: TreeNode[], collapsed: Set<string>, depth = 0): Row[] {
  const rows: Row[] = [];
  for (const node of nodes) {
    if (node.isDirectory) {
      const open = !collapsed.has(node.path);
      rows.push({ key: node.path, name: node.name, depth, path: null, kind: 'folder', open });
      if (open) {
        rows.push(...flatten(node.children ?? [], collapsed, depth + 1));
      }
    } else {
      rows.push({
        key: node.path,
        name: node.name,
        depth,
        path: node.kind === 'notebook' ? node.path : null,
        kind: node.kind === 'jobs' ? 'jobs' : 'notebook',
      });
    }
  }
  return rows;
}

/**
 * The editor's file sidebar. It exists only here and on the Notebooks page —
 * a tree on the dashboard would be navigation furniture nobody asked for.
 *
 * Collapsed it becomes a 16px strip rather than disappearing, so there is
 * always something to click to get it back.
 */
export function NotebookExplorer({
  path,
  branch,
  width,
  collapsed,
  onCollapse,
}: {
  /** The notebook currently open, highlighted in the tree. */
  path: string;
  /** The branch that notebook is open on, so the tree shows the same files. */
  branch: string;
  width: number;
  collapsed: boolean;
  onCollapse: (collapsed: boolean) => void;
}) {
  const navigate = useNavigate();
  const { data, reload } = usePolling(() => api.notebooks(), null);
  const [env, setEnv] = useState('');
  const [shut, setShut] = useState<Set<string>>(new Set());
  const mayCreate = useIsProjectMember();

  const environments = (data?.environments ?? []).filter((e) => e.tree != null);
  // Follow the open notebook's branch, so the tree is the files you are actually
  // looking at. Guarded because a branch can be one this list does not carry:
  // somebody else's is readable by URL but is nobody's tree to browse.
  useEffect(() => {
    if (environments.some((e) => e.name === branch)) {
      setEnv(branch);
    } else if (!env && environments.length > 0) {
      setEnv(environments[0].name);
    }
  }, [environments.length, branch]);

  /** Makes it on your branch and opens it there — never on the one being read. */
  async function create() {
    const wanted = promptForNotebook();
    if (wanted == null) {
      return;
    }
    try {
      await createNotebook(wanted);
      reload();
      navigate(editHref(wanted, 'mine'));
    } catch (e) {
      // ponytail: the sidebar has nowhere to put a banner. A dialog if this ever
      // needs to say more than one sentence.
      alert((e as Error).message);
    }
  }

  if (collapsed) {
    return (
      <button
        type="button"
        onClick={() => onCollapse(false)}
        title="Show explorer"
        aria-label="Show explorer"
        className="flex w-[16px] shrink-0 items-center justify-center border-r border-border bg-muted text-muted-subtle outline-none hover:bg-surface-panel-strong hover:text-primary"
      >
        <ChevronRight className="size-3" aria-hidden="true" />
      </button>
    );
  }

  const selected = environments.find((e) => e.name === env);
  const rows = flatten(selected?.tree?.children ?? [], shut);

  return (
    <div
      className="flex shrink-0 flex-col overflow-hidden border-r border-border bg-muted"
      style={{ width: `${width}px` }}
    >
      <div className="flex items-center justify-between py-2 pl-3.5 pr-2.5 text-xs font-semibold tracking-[0.06em] text-muted-subtle">
        <span>EXPLORER</span>
        <button
          type="button"
          onClick={() => onCollapse(true)}
          aria-label="Hide explorer"
          className="rounded-sm border border-input px-1 outline-none hover:border-primary hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ChevronLeft className="size-3" aria-hidden="true" />
        </button>
      </div>

      <div className="flex items-center gap-1.5 pb-2.5 pl-3.5 pr-2.5">
        <GitBranch className="size-[13px] shrink-0 text-muted-subtle" aria-hidden="true" />
        <Select
          value={env}
          onValueChange={(branch) => {
            setEnv(branch);
            // The same memory the Notebooks page keeps: picking a branch in
            // either place is picking it for the project.
            saveBranch(projectSlug(), branch);
          }}
        >
          <SelectTrigger
            size="sm"
            className="min-w-0 flex-1 bg-card font-mono text-xs"
            aria-label="Environment"
          >
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {environments.map((environment) => (
              <SelectItem key={environment.name} value={environment.name} className="font-mono">
                {environment.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {mayCreate && (
          <button
            type="button"
            onClick={create}
            title="New notebook"
            aria-label="New notebook"
            className="shrink-0 rounded-sm border border-input p-1 text-muted-subtle outline-none hover:border-primary hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
          >
            <FilePlus2 className="size-3.5" aria-hidden="true" />
          </button>
        )}
      </div>

      <div className="min-h-0 flex-1 overflow-auto">
        {rows.map((row) => {
          const active = row.path != null && row.path === path;
          return (
            <button
              key={row.key}
              type="button"
              disabled={row.kind === 'jobs'}
              onClick={() =>
                row.kind === 'folder'
                  ? setShut((current) => {
                      const next = new Set(current);
                      next.has(row.key) ? next.delete(row.key) : next.add(row.key);
                      return next;
                    })
                  : row.path && navigate(editHref(row.path, env))
              }
              // The 2px left edge is a border on every row, transparent unless
              // selected: colouring it in place keeps the text from shifting
              // two pixels sideways as the selection moves.
              className={[
                'flex w-full items-center gap-1.5 border-l-2 py-[3px] pr-2.5 text-left outline-none',
                row.kind === 'folder' ? 'text-base font-medium' : 'font-mono text-xs',
                row.kind === 'jobs' ? 'text-muted-subtle' : '',
                active
                  ? 'border-l-primary bg-surface-panel-strong font-semibold text-foreground'
                  : 'border-l-transparent hover:bg-surface-panel-strong',
              ].join(' ')}
              style={{ paddingLeft: `${10 + row.depth * 12}px` }}
            >
              <span aria-hidden="true" className="w-[11px] shrink-0 text-[9px] text-muted-subtle">
                {row.kind === 'folder' ? (row.open ? '▾' : '▸') : ''}
              </span>
              <span className="truncate">{row.name}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}
