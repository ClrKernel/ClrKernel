import { ChevronDown, ChevronRight, FolderClosed, SquarePlus } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api';
import { changedTree, type Change } from '../commitTree';
import { fileLanguage } from '../notebook';
import { DiffView } from './DiffView';
import { ErrorBanner, usePolling } from './common';
import { FileBadge } from './FileBadge';

/** The letter git gives a change, and how the file's name should read because of it. */
function letter(status: string): string {
  return status.trim().charAt(0).toUpperCase();
}

/**
 * A changed file's name, drawn as what happened to it.
 *
 * <p>Deleted is struck through, because the name is of something that is no
 * longer there — a colour alone says "this row is special" without saying what,
 * and the list is read at a glance. Added gets a plus in place of the file-type
 * badge for the same reason.</p>
 */
export function ChangedName({ change, name }: { change: Change; name: string }) {
  const kind = letter(change.status);
  return (
    <>
      {kind === 'A'
        ? <SquarePlus className="size-3.5 shrink-0 text-status-ok" aria-hidden="true" />
        : <FileBadge name={name} />}
      <span className={['truncate', kind === 'D' ? 'text-muted-subtle line-through' : ''].join(' ')}>
        {name}
      </span>
    </>
  );
}

/** What one commit did to one file, in the side-by-side editor the app diffs in. */
export function CommitFileDiff({ branch, sha, change }: {
  branch: string; sha: string; change: Change;
}) {
  const { data, error } = usePolling(
    () => api.commitFile(branch, sha, change.path, change.oldPath),
    null, [branch, sha, change.path]);

  if (error != null) {
    return <ErrorBanner error={error} />;
  }
  if (data == null) {
    return <p className="text-base text-muted-foreground">Loading the change…</p>;
  }
  return (
    <>
      <p className="mb-2 shrink-0 text-base text-muted-foreground">
        {/* The two cases where a side is not a version of the file but its
            absence — an empty left pane otherwise reads as "it was blank". */}
        {data.before == null
          ? 'Added by this commit.'
          : data.after == null
            ? 'Deleted by this commit.'
            : 'Before (left) and after (right).'}
        {change.oldPath && (
          <> Renamed <code className="font-mono text-code">{change.oldPath}</code>
            {' → '}<code className="font-mono text-code">{change.path}</code>.</>
        )}
      </p>
      <DiffView
        original={data.before ?? ''}
        modified={data.after ?? ''}
        language={fileLanguage(change.path)}
      />
    </>
  );
}

/**
 * Every file the commit touched, one card each, the diff inside.
 *
 * <p>The page you get before picking a file, and the same summary a pull request
 * opens on: the answer to "what did this commit do" is usually all of it at once,
 * not one file at a time.</p>
 *
 * <p>Collapsed by default and fetched on opening. A commit that rewrites forty
 * files would otherwise be forty requests before the page can be read at all.</p>
 */
export function CommitFileCards({ branch, sha, files }: {
  branch: string; sha: string; files: Change[];
}) {
  const [open, setOpen] = useState<Set<string>>(new Set());
  return (
    <ul aria-label="Changes" className="flex flex-col gap-2">
      {files.map((change) => {
        const shown = open.has(change.path);
        return (
          <li key={change.path} className="rounded-lg border border-border bg-card">
            <button
              type="button"
              aria-expanded={shown}
              onClick={() => setOpen((current) => {
                const next = new Set(current);
                next.has(change.path) ? next.delete(change.path) : next.add(change.path);
                return next;
              })}
              className="flex w-full items-center gap-1.5 px-3 py-2 text-left font-mono text-code outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {shown
                ? <ChevronDown className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />
                : <ChevronRight className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />}
              <ChangedName change={change} name={change.path} />
              <span className="ml-auto shrink-0 pl-3 text-xs text-muted-subtle">
                {STATUS_WORD[letter(change.status)] ?? change.status.trim()}
              </span>
            </button>
            {/* A height, because Monaco has none of its own: `.diff-editor` is
                `flex: 1` and would collapse to nothing inside a card that grows
                to fit its content. */}
            {shown && (
              <div className="flex h-[26rem] flex-col border-t border-border px-3 py-2">
                <CommitFileDiff branch={branch} sha={sha} change={change} />
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}

const STATUS_WORD: Record<string, string> = {
  A: 'added', M: 'modified', D: 'deleted', R: 'renamed',
};

/**
 * The commit page's left pane: the files this commit touched, and nothing else.
 *
 * <p>The same shape as the file explorer beside it in the editor, so moving
 * between the two is not a change of vocabulary — but scoped to one commit, which
 * is what makes it a reading of the commit rather than of the branch.</p>
 */
export function ChangedFiles({ files, hrefFor, selected }: {
  files: Change[];
  hrefFor: (change: Change) => string;
  /** The file open beside it, highlighted. Null on the summary. */
  selected: string | null;
}) {
  return (
    <div className="min-h-0 flex-1 overflow-auto pb-8">
      {changedTree(files).map((row) => (
        row.change == null ? (
          <div
            key={row.key}
            className="flex items-center gap-1.5 border-l-2 border-l-transparent py-[3px] pr-2.5 text-base font-medium"
            style={{ paddingLeft: `${10 + row.depth * 12}px` }}
          >
            <FolderClosed className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span className="truncate">{row.name}</span>
          </div>
        ) : (
          <Link
            key={row.key}
            to={hrefFor(row.change)}
            title={row.change.oldPath ? `renamed from ${row.change.oldPath}` : row.change.path}
            className={[
              'flex w-full items-center gap-1.5 border-l-2 py-[3px] pr-2.5 text-left font-mono text-xs outline-none',
              row.change.path === selected
                ? 'border-l-primary bg-surface-panel-strong font-semibold text-foreground'
                : 'border-l-transparent hover:bg-surface-panel-strong',
            ].join(' ')}
            style={{ paddingLeft: `${10 + row.depth * 12}px` }}
          >
            <ChangedName change={row.change} name={row.name} />
          </Link>
        )
      ))}
      {files.length === 0 && (
        <p className="px-3 py-2 text-base text-muted-subtle">
          No files — a merge's changes are the commits it brought in.
        </p>
      )}
    </div>
  );
}
