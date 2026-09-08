import { ChevronRight, FolderClosed } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { api, projectSlug } from '../api';
import { timeAgo } from '../ipynb';
import { editPath } from '../routes';
import { CommitList } from './CommitList';
import { ErrorBanner, usePolling } from './common';
import { FileBadge } from './FileBadge';

/** Bytes, at the precision anybody actually reads. */
function size(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const kb = bytes / 1024;
  return kb < 1024 ? `${kb.toFixed(1)} KB` : `${(kb / 1024).toFixed(1)} MB`;
}

/**
 * The Files route's main pane: what is in this branch, and what has happened to
 * it.
 *
 * <p>It replaced an empty pane with a New notebook button — which said nothing
 * about the project you had just opened. The explorer on the left is for moving
 * between files; this is for reading the repo, which is a different act and the
 * one every git host puts on its front page.</p>
 */
export function RepoBrowser({ branch }: { branch: string }) {
  const [tab, setTab] = useState<'contents' | 'history'>('contents');
  /** Which folder Contents is showing. Not in the URL: it is a view of the page,
   *  and the file you open from it is what earns an address. */
  const [folder, setFolder] = useState('');

  const { data: contents, error: contentsError } = usePolling(
    () => api.contents(branch, folder), null, [branch, folder]);
  const { data: history, error: historyError } = usePolling(
    () => (tab === 'history' ? api.commits(branch, 50, true) : Promise.resolve(null)),
    null, [branch, tab]);

  // Reset on a branch change: `reports/` may not be there on the other one, and a
  // listing of a folder that is gone is an empty table with no explanation.
  const [lastBranch, setLastBranch] = useState(branch);
  if (lastBranch !== branch) {
    setLastBranch(branch);
    setFolder('');
  }

  const crumbs = folder === '' ? [] : folder.split('/');

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <Tabs value={tab} onValueChange={(next) => setTab(next as 'contents' | 'history')}>
        <TabsList variant="line">
          <TabsTrigger value="contents">Contents</TabsTrigger>
          <TabsTrigger value="history">History</TabsTrigger>
        </TabsList>
      </Tabs>

      <ErrorBanner error={tab === 'contents' ? contentsError : historyError} />

      {tab === 'contents' ? (
        <div className="mt-3 flex min-h-0 flex-1 flex-col">
          {/* Where you are, and the way back up. */}
          <div className="mb-2 flex flex-wrap items-center gap-1 text-base">
            <button
              type="button"
              className="text-primary hover:underline disabled:text-muted-foreground disabled:no-underline"
              disabled={folder === ''}
              onClick={() => setFolder('')}
            >
              {projectSlug()}
            </button>
            {crumbs.map((crumb, i) => (
              <span key={crumb + i} className="flex items-center gap-1">
                <ChevronRight className="size-3 text-muted-subtle" aria-hidden="true" />
                <button
                  type="button"
                  className="text-primary hover:underline disabled:text-foreground disabled:no-underline"
                  disabled={i === crumbs.length - 1}
                  onClick={() => setFolder(crumbs.slice(0, i + 1).join('/'))}
                >
                  {crumb}
                </button>
              </span>
            ))}
          </div>

          <div className="table-box min-h-0 overflow-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Last commit</th>
                  <th>When</th>
                  <th className="text-right">Size</th>
                </tr>
              </thead>
              <tbody>
                {(contents?.entries ?? []).map((entry) => (
                  <tr key={entry.path}>
                    <td>
                      <span className="flex items-center gap-1.5">
                        {entry.isDirectory ? (
                          <>
                            <FolderClosed className="size-3.5 shrink-0 text-muted-foreground"
                              aria-hidden="true" />
                            <button
                              type="button"
                              className="text-primary hover:underline"
                              onClick={() => setFolder(entry.path)}
                            >
                              {entry.name}
                            </button>
                          </>
                        ) : (
                          <>
                            <FileBadge name={entry.name} />
                            <Link
                              className="font-mono text-code hover:underline"
                              to={editPath(projectSlug(), branch, entry.path)}
                            >
                              {entry.name}
                            </Link>
                          </>
                        )}
                      </span>
                    </td>
                    {/* The column the filesystem cannot answer: not when it
                        changed, but why. */}
                    <td className="max-w-[38ch] truncate text-muted-foreground">
                      {entry.lastCommit?.subject ?? '—'}
                      {entry.lastCommit && (
                        <span className="text-muted-subtle"> · {entry.lastCommit.author}</span>
                      )}
                    </td>
                    <td className="whitespace-nowrap text-muted-subtle">
                      {timeAgo(entry.lastCommit?.when ?? entry.modified)}
                    </td>
                    <td className="whitespace-nowrap text-right text-muted-subtle">
                      {entry.isDirectory ? '' : size(entry.size)}
                    </td>
                  </tr>
                ))}
                {contents != null && contents.entries.length === 0 && (
                  <tr>
                    <td colSpan={4} className="text-muted-subtle">
                      {folder === '' ? 'Nothing in this branch yet.' : 'This folder is empty.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      ) : (
        <div className="mt-3 min-h-0 flex-1 overflow-auto pb-4">
          {history == null ? (
            <p className="text-base text-muted-foreground">Reading the history…</p>
          ) : (
            <CommitList
              commits={history.commits}
              empty="No commits on this branch yet."
              expandable
            />
          )}
        </div>
      )}
    </div>
  );
}
