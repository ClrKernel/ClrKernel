import { useEffect, useState } from 'react';
import { api, type ApiCommit } from '../api';
import { timeAgo } from '../ipynb';
import { fileLanguage } from '../notebook';
import { DiffView } from './DiffView';
import { ErrorBanner, usePolling } from './common';

/**
 * The rail beside a file's commits.
 *
 * <p>A dot per commit and a line joining them, with a merge drawn hollow. It is
 * deliberately not a lane graph: this list is <em>filtered</em> to one file, so
 * two rows next to each other are usually not parent and child, and lanes drawn
 * across them would claim a shape the data does not have. What the rail does say
 * is true — this is the order the commits touched this file, newest first, and
 * this one came in on a merge.</p>
 */
function Rail({ merge, first, last }: { merge: boolean; first: boolean; last: boolean }) {
  return (
    <svg width="18" height="100%" viewBox="0 0 18 40" preserveAspectRatio="none"
      className="shrink-0 self-stretch" aria-hidden="true">
      {!first && <line x1="9" y1="0" x2="9" y2="20" className="stroke-border" strokeWidth="2" />}
      {!last && <line x1="9" y1="20" x2="9" y2="40" className="stroke-border" strokeWidth="2" />}
      {merge
        ? <circle cx="9" cy="20" r="4.5" className="fill-background stroke-primary" strokeWidth="2" />
        : <circle cx="9" cy="20" r="4" className="fill-primary" />}
    </svg>
  );
}

/**
 * What one commit did to this file, as git named it there.
 *
 * <p>The history follows renames, so the path is per commit rather than the one
 * in the address bar — that is the whole point of following, and reading the
 * pre-rename commits at today's path would find nothing.</p>
 *
 * <p>A rename is preferred when git reports more than one row, because it is the
 * row that carries both sides. Undefined when there is no row at all, which is a
 * refusal and never a reason to fall back to the current path: that fallback is
 * exactly the half-following this view exists to avoid.</p>
 */
function changeOf(commit: ApiCommit): ApiCommit['files'][number] | undefined {
  return commit.files.find((f) => f.status.startsWith('R')) ?? commit.files[0];
}

/**
 * One file's history: the commits that touched it, and what each one did.
 *
 * <p>The diff is the same side-by-side editor the branch comparisons use, over
 * the file as it stood on either side of the chosen commit — so "what changed
 * when" is read the same way here as "what differs between branches", which is
 * the only reason to have two of them at all.</p>
 */
export function FileHistory({ branch, path }: { branch: string; path: string }) {
  const [selected, setSelected] = useState<ApiCommit | null>(null);
  const [sides, setSides] = useState<{ before: string | null; after: string | null } | null>(null);
  const [diffError, setDiffError] = useState<string | null>(null);

  const { data, error } = usePolling(
    () => api.commits(branch, 100, true, path), null, [branch, path]);

  // The newest commit, chosen for you: a history that opens on nothing makes you
  // click before it has told you anything.
  const commits = data?.commits ?? [];
  useEffect(() => setSelected(commits[0] ?? null), [data, path, branch]);

  const change = selected == null ? undefined : changeOf(selected);

  useEffect(() => {
    if (selected == null) {
      return;
    }
    let live = true;
    setSides(null);
    setDiffError(null);
    if (change == null) {
      setDiffError(
        `Git did not say what ${selected.shortSha} did to this file, so there is `
        + 'nothing to compare. Open the commit itself.');
      return;
    }
    api
      .commitFile(branch, selected.sha, change.path, change.oldPath)
      .then((r) => live && setSides({ before: r.before, after: r.after }))
      .catch((e) => live && setDiffError((e as Error).message));
    return () => {
      live = false;
    };
  }, [selected?.sha, branch, path]);

  if (error != null) {
    return <div className="px-4"><ErrorBanner error={error} /></div>;
  }
  if (data == null) {
    return <p className="px-4 text-base text-muted-foreground">Reading the history…</p>;
  }
  if (commits.length === 0) {
    return (
      <p className="px-4 text-base text-muted-foreground">
        No commits touch this file yet — it is saved on this branch but has not been pushed.
      </p>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 gap-3 px-4 pb-4">
      {/* The list, then the change. Narrow enough that the diff keeps the room. */}
      <ol className="w-[19rem] shrink-0 overflow-auto">
        {commits.map((commit, i) => {
          const active = selected?.sha === commit.sha;
          const renamedFrom = changeOf(commit)?.oldPath;
          return (
            <li key={commit.sha} className="flex items-stretch gap-1.5">
              <Rail
                merge={commit.parents.length > 1}
                first={i === 0}
                last={i === commits.length - 1}
              />
              <button
                type="button"
                onClick={() => setSelected(commit)}
                className={[
                  'my-0.5 min-w-0 flex-1 rounded-md border px-2 py-1.5 text-left outline-none',
                  'focus-visible:ring-2 focus-visible:ring-ring',
                  active
                    ? 'border-primary bg-surface-panel-strong'
                    : 'border-transparent hover:bg-surface-panel-strong',
                ].join(' ')}
              >
                <span className="block truncate text-base font-medium">
                  {commit.subject || '(no message)'}
                </span>
                <span className="mt-0.5 block truncate text-xs text-muted-subtle">
                  <code className="font-mono">{commit.shortSha}</code>
                  {' · '}{commit.author}{' · '}{timeAgo(commit.when)}
                  {commit.parents.length > 1 && ' · merge'}
                </span>
                {/* The row where the name changed. Without it a followed history
                    is a list of commits about a file that was not called this
                    yet, with nothing saying so. */}
                {renamedFrom && (
                  <span className="mt-0.5 block truncate text-xs text-muted-subtle">
                    renamed from <code className="font-mono">{renamedFrom}</code>
                  </span>
                )}
              </button>
            </li>
          );
        })}
      </ol>

      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        <ErrorBanner error={diffError} />
        {selected == null || (sides == null && diffError == null) ? (
          <p className="text-base text-muted-foreground">Loading the change…</p>
        ) : sides == null ? null : (
          <>
            <p className="mb-2 shrink-0 text-base text-muted-foreground">
              {/* Which way round the two sides are, and the two cases where one
                  of them is not a version of the file but its absence. */}
              {sides.before == null
                ? <>Added by <code className="font-mono text-code">{selected.shortSha}</code>.</>
                : sides.after == null
                  ? <>Deleted by <code className="font-mono text-code">{selected.shortSha}</code>.</>
                  : <>
                      Before (left) and after (right){' '}
                      <code className="font-mono text-code">{selected.shortSha}</code>.
                    </>}
              {/* Two different filenames over a side-by-side diff read as a
                  whole-file rewrite unless something says they are the same file. */}
              {change?.oldPath && (
                <>
                  {' '}Renamed <code className="font-mono text-code">{change.oldPath}</code>
                  {' → '}<code className="font-mono text-code">{change.path}</code>.
                </>
              )}
              {' '}{selected.author} · {timeAgo(selected.when)}
            </p>
            <DiffView
              original={sides.before ?? ''}
              modified={sides.after ?? ''}
              language={fileLanguage(change?.path ?? path)}
            />
          </>
        )}
      </div>
    </div>
  );
}
