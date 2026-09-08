import { ArrowLeft, ChevronLeft } from 'lucide-react';
import { useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, projectSlug } from '../api';
import { CollapsedRail, ErrorBanner, usePolling } from '../components/common';
import {
  ChangedFiles, CommitFileCards, CommitFileDiff,
} from '../components/CommitFiles';
import { shortSha } from '../components/CommitList';
import { Splitter } from '../components/Splitter';
import { stamp } from '../ipynb';
import {
  DEFAULT_LAYOUT, MAX_EXPLORER, MIN_EXPLORER, clamp, loadLayout, saveLayout,
  type LayoutPrefs,
} from '../prefs';
import { commitPath, historyPath, pathFromSplat } from '../routes';

/**
 * One commit: what it changed, and what each change was.
 *
 * <p>A page rather than a row that expands. The old History list opened a commit
 * in place, which left the files inside a list item with nowhere to go from
 * there and no address to send anybody — a commit is a thing people link to.</p>
 *
 * <p>The same three parts as the editor beside it: a file pane, a gutter, and
 * whatever fills the rest. Here the file pane is only the files this commit
 * touched, and the rest is either every change at once or the one you picked.</p>
 */
export function CommitDetail() {
  const { branch = 'mine', sha = '' } = useParams<{ branch: string; sha: string }>();
  const params = useParams();
  // The file, if one is chosen — the tail of the URL after the sha.
  const chosen = pathFromSplat(params['*']);
  const [layout, setLayout] = useState<LayoutPrefs>(() => loadLayout());
  const shell = useRef<HTMLDivElement>(null);

  const { data: commit, error } = usePolling(
    () => api.commit(branch, sha), null, [branch, sha]);

  function move(next: LayoutPrefs) {
    setLayout(next);
    saveLayout(next);
  }

  const files = commit?.files ?? [];
  const open = chosen === '' ? null : files.find((f) => f.path === chosen) ?? null;

  return (
    <div className="flex min-h-0 flex-1 overflow-hidden" ref={shell}>
      {layout.explorerCollapsed ? (
        <CollapsedRail label="Show changed files"
          onExpand={() => move({ ...layout, explorerCollapsed: false })} />
      ) : (
        <div
          aria-label="Changed files"
          className="flex shrink-0 flex-col overflow-hidden border-r border-border bg-muted"
          style={{ width: `${layout.explorerWidth}px` }}
        >
          <div className="flex items-center justify-between py-2 pl-3.5 pr-2.5 text-xs font-semibold tracking-[0.06em] text-muted-subtle">
            <span>CHANGED FILES</span>
            <button
              type="button"
              onClick={() => move({ ...layout, explorerCollapsed: true })}
              aria-label="Hide changed files"
              className="rounded-sm border border-input px-1 outline-none hover:border-primary hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
            >
              <ChevronLeft className="size-3" aria-hidden="true" />
            </button>
          </div>
          <ChangedFiles
            files={files}
            selected={open?.path ?? null}
            hrefFor={(change) => commitPath(projectSlug(), branch, sha, change.path)}
          />
        </div>
      )}
      {!layout.explorerCollapsed && (
        <Splitter
          orientation="vertical"
          label="Changed files width"
          onDrag={(clientX) => move({
            ...layout,
            explorerWidth: clamp(
              clientX - (shell.current?.getBoundingClientRect().left ?? 0),
              MIN_EXPLORER, MAX_EXPLORER),
          })}
          onReset={() => move({ ...layout, explorerWidth: DEFAULT_LAYOUT.explorerWidth })}
        />
      )}

      <div className="flex min-w-0 flex-1 flex-col overflow-hidden px-7 py-5">
        <ErrorBanner error={error} />

        <div className="mb-3 shrink-0">
          <Link
            to={historyPath(projectSlug(), branch)}
            className="inline-flex items-center gap-1 text-base text-primary hover:underline"
          >
            <ArrowLeft className="size-3.5" aria-hidden="true" />
            History
          </Link>
          {commit && (
            <>
              <h1 className="mt-1 text-lg font-semibold">
                {commit.subject || '(no message)'}
              </h1>
              <p className="text-base text-muted-subtle">
                <code className="font-mono text-code text-primary">{shortSha(commit.sha)}</code>
                {' · '}{commit.author}{' · '}{stamp(commit.when)}
                {commit.parents.length > 1 && ' · merge, against its first parent'}
              </p>
            </>
          )}
        </div>

        {commit == null && error == null ? (
          <p className="text-base text-muted-foreground">Reading the commit…</p>
        ) : commit == null ? null : open != null ? (
          // One file, read the way every other comparison in the app is read.
          <div className="flex min-h-0 flex-1 flex-col">
            <p className="mb-1 shrink-0 font-mono text-code">{open.path}</p>
            <CommitFileDiff branch={branch} sha={sha} change={open} />
          </div>
        ) : chosen !== '' ? (
          <p className="text-base text-muted-foreground">
            <code className="font-mono text-code">{chosen}</code> is not one of the files this
            commit changed.
          </p>
        ) : (
          <div className="min-h-0 flex-1 overflow-auto pb-4">
            <CommitFileCards branch={branch} sha={sha} files={files} />
          </div>
        )}
      </div>
    </div>
  );
}
