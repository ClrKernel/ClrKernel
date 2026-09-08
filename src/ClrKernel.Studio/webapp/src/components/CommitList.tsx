import { FileDiff } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { ApiCommit } from '../api';
import { stamp, timeAgo } from '../ipynb';
import { CommitRail } from './CommitRail';

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
 * The first eight of a sha, for reading. The URL keeps all forty: an abbreviation
 * is unambiguous until the repository grows, and a link that stops working at some
 * size is a worse trade than a long address.
 */
export function shortSha(sha: string): string {
  return sha.slice(0, 8);
}

/**
 * One commit as a link: the message, and under it the sha, who wrote it and
 * when.
 *
 * <p>It used to expand in place to list its files. That put the answer to "what
 * did this change" inside a row in a list, with nowhere to go from there and no
 * address to send anybody — so it is a page now, and this is the way in.</p>
 *
 * <p>The date is written out rather than "2d ago". A history is a record, and
 * "which afternoon was that" is a question relative time cannot answer.</p>
 */
function CommitRow({ commit, href, first, last }: {
  commit: ApiCommit;
  href: string;
  first: boolean;
  last: boolean;
}) {
  const merge = commit.parents.length > 1;
  return (
    <li className="flex items-stretch gap-1.5">
      <CommitRail merge={merge} first={first} last={last} />
      <Link
        to={href}
        className={[
          'my-0.5 min-w-0 flex-1 rounded-lg border border-border bg-card px-3 py-2 outline-none',
          'hover:border-primary focus-visible:ring-2 focus-visible:ring-ring',
        ].join(' ')}
      >
        <span className="block truncate text-base font-medium">
          {commit.subject || '(no message)'}
        </span>
        <span className="mt-0.5 block truncate text-xs text-muted-subtle">
          <code className="font-mono text-primary">{shortSha(commit.sha)}</code>
          {' · '}{commit.author}{' · '}{stamp(commit.when)}
          {/* Two parents is a merge, which is worth saying: its file list is
              against the first parent, so it is not the whole story. */}
          {merge && ' · merge'}
        </span>
      </Link>
    </li>
  );
}

/** One commit, opened out — the merge preview, where every arriving commit is
 *  already the question being asked. */
function Commit({ commit }: { commit: ApiCommit }) {
  const merge = commit.parents.length > 1;
  return (
    <li className="rounded-lg border border-border bg-card px-3 py-2">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-left">
        <code className="font-mono text-xs text-primary">{commit.shortSha}</code>
        <span className="text-base font-medium">{commit.subject || '(no message)'}</span>
        <span className="ml-auto whitespace-nowrap text-xs text-muted-subtle">
          {commit.author} · {timeAgo(commit.when)}
          {merge && ' · merge'}
        </span>
      </div>

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
  href,
}: {
  commits: ApiCommit[];
  empty?: string;
  /**
   * Where each commit goes. Given, the list is a graph of links and the files
   * live on the page at the other end; omitted, each commit is opened out in
   * place — which is the merge preview, where the files arriving *are* the
   * question and there is nowhere else to send you.
   */
  href?: (commit: ApiCommit) => string;
}) {
  if (commits.length === 0) {
    return <p className="text-base text-muted-foreground">{empty}</p>;
  }
  if (href == null) {
    return (
      <ol className="flex flex-col gap-2">
        {commits.map((commit) => <Commit key={commit.sha} commit={commit} />)}
      </ol>
    );
  }
  return (
    <ol className="flex flex-col">
      {commits.map((commit, i) => (
        <CommitRow
          key={commit.sha}
          commit={commit}
          href={href(commit)}
          first={i === 0}
          last={i === commits.length - 1}
        />
      ))}
    </ol>
  );
}
