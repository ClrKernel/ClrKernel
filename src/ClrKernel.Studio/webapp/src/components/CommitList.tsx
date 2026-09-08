import { ChevronDown, ChevronRight, FileDiff } from 'lucide-react';
import { useState } from 'react';
import type { ApiCommit } from '../api';
import { timeAgo } from '../ipynb';

/** The letter git gives a change, spelled out. */
const STATUS: Record<string, { label: string; tone: string }> = {
  A: { label: 'added', tone: 'text-status-ok' },
  M: { label: 'modified', tone: 'text-status-warning' },
  D: { label: 'deleted', tone: 'text-status-danger' },
  R: { label: 'renamed', tone: 'text-muted-foreground' },
  '?': { label: 'untracked', tone: 'text-muted-subtle' },
};

export function FileChange({ status, path }: { status: string; path: string }) {
  // git says R100 for a rename; the letter is the part that means anything here.
  const key = status.trim().charAt(0).toUpperCase();
  const known = STATUS[key] ?? { label: status.trim(), tone: 'text-muted-subtle' };
  return (
    <li className="flex items-baseline gap-2">
      <span className={`w-[4.5rem] shrink-0 text-right text-xs ${known.tone}`}>{known.label}</span>
      <span className="truncate font-mono text-xs">{path}</span>
    </li>
  );
}

/**
 * One commit. Expandable where the caller says so — History, where a subject
 * line is a summary and the files are the answer to "what did that actually
 * change"; flat in the merge preview, where everything arriving is already open
 * because that is the whole question being asked.
 */
function Commit({ commit, expandable }: { commit: ApiCommit; expandable: boolean }) {
  const [open, setOpen] = useState(!expandable);
  const merge = commit.parents.length > 1;

  const head = (
    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-left">
      {expandable && (
        open
          ? <ChevronDown className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />
          : <ChevronRight className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />
      )}
      <code className="font-mono text-xs text-primary">{commit.shortSha}</code>
      <span className="text-base font-medium">{commit.subject || '(no message)'}</span>
      <span className="ml-auto whitespace-nowrap text-xs text-muted-subtle">
        {commit.author} · {timeAgo(commit.when)}
        {/* Two parents is a merge, which is worth saying: its file list is
            against the first parent, so it is not the whole story. */}
        {merge && ' · merge'}
      </span>
    </div>
  );

  return (
    <li className="rounded-lg border border-border bg-card px-3 py-2">
      {expandable ? (
        <button
          type="button"
          className="w-full outline-none focus-visible:ring-2 focus-visible:ring-ring"
          aria-expanded={open}
          onClick={() => setOpen(!open)}
        >
          {head}
        </button>
      ) : head}

      {open && (
        <>
          {commit.files.length > 0 && (
            <ul className="mt-1.5 flex flex-col gap-0.5 border-t border-border pt-1.5">
              {commit.files.map((file) => (
                <FileChange key={file.path} status={file.status} path={file.path} />
              ))}
            </ul>
          )}
          {commit.files.length === 0 && (
            <p className="mt-1.5 flex items-center gap-1.5 border-t border-border pt-1.5 text-xs text-muted-subtle">
              <FileDiff className="size-3 shrink-0" aria-hidden="true" />
              {merge
                ? 'a merge — its changes are the commits it brought in'
                : 'no files changed'}
            </p>
          )}
          {expandable && (
            <p className="mt-1.5 font-mono text-xs text-muted-subtle">{commit.sha}</p>
          )}
        </>
      )}
    </li>
  );
}

/**
 * A list of commits, newest first — the history view, and the body of the merge
 * preview.
 */
export function CommitList({
  commits,
  empty = 'No commits yet.',
  expandable = false,
}: {
  commits: ApiCommit[];
  empty?: string;
  /** Collapse each commit to its subject, and open it on click. */
  expandable?: boolean;
}) {
  if (commits.length === 0) {
    return <p className="text-base text-muted-foreground">{empty}</p>;
  }
  return (
    <ol className="flex flex-col gap-2">
      {commits.map((commit) => (
        <Commit key={commit.sha} commit={commit} expandable={expandable} />
      ))}
    </ol>
  );
}
