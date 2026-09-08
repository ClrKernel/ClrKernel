import { FileDiff } from 'lucide-react';
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
 * A list of commits, newest first — the history view, and the body of the merge
 * preview.
 *
 * Files are shown when the commit carries them: history asks git for the subjects
 * alone, and a preview asks for the name-status too, so one component covers both
 * without a flag saying which it is.
 */
export function CommitList({
  commits,
  empty = 'No commits yet.',
}: {
  commits: ApiCommit[];
  empty?: string;
}) {
  if (commits.length === 0) {
    return <p className="text-base text-muted-foreground">{empty}</p>;
  }
  return (
    <ol className="flex flex-col gap-3">
      {commits.map((commit) => (
        <li key={commit.sha} className="rounded-lg border border-border bg-card px-3 py-2">
          <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
            <code className="font-mono text-xs text-primary">{commit.shortSha}</code>
            <span className="text-base font-medium">{commit.subject || '(no message)'}</span>
            <span className="ml-auto whitespace-nowrap text-xs text-muted-subtle">
              {commit.author} · {timeAgo(commit.when)}
              {/* Two parents is a merge, which is worth saying: its file list is
                  against the first parent, so it is not the whole story. */}
              {commit.parents.length > 1 && ' · merge'}
            </span>
          </div>
          {commit.files.length > 0 && (
            <ul className="mt-1.5 flex flex-col gap-0.5 border-t border-border pt-1.5">
              {commit.files.map((file) => (
                <FileChange key={file.path} status={file.status} path={file.path} />
              ))}
            </ul>
          )}
          {commit.files.length === 0 && commit.parents.length > 1 && (
            <p className="mt-1 flex items-center gap-1.5 text-xs text-muted-subtle">
              <FileDiff className="size-3" aria-hidden="true" />
              a merge — its changes are the commits it brought in
            </p>
          )}
        </li>
      ))}
    </ol>
  );
}
