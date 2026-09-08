import { CircleAlert, Upload } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { api, projectSlug, type ApiIncoming } from '../api';
import { editPath } from '../routes';
import { CommitList } from './CommitList';
import { ErrorBanner, usePolling } from './common';
import { FileChange } from './CommitList';
import { Modal } from './Modal';

/**
 * Publishing: what is about to leave your branch, and what is stopping it.
 *
 * <p>It replaced a text field on the toolbar whose only failure message was
 * "2 jobs files have problems — fix them before pushing to test". That named no
 * file, no line and no problem, and it arrived <em>after</em> you had typed a
 * commit message — so the one thing you needed in order to act on it was the one
 * thing it did not say.</p>
 *
 * <p>Now the problems are read before you start, each with its file and the line
 * it is on, and each file's name is a link straight to it. The blocked case is
 * the reason this is a dialog at all.</p>
 */
export function PublishDialog({ onClose, onPublished }: {
  onClose: () => void;
  onPublished: (message: string) => void;
}) {
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [staged, setStaged] = useState<Set<string> | null>(null);
  const { data, error: loadError, reload } = usePolling<ApiIncoming>(
    () => api.incoming(true), null);

  const uncommitted = data?.uncommitted ?? [];
  const outgoing = data?.outgoing ?? [];
  const problems = data?.problems ?? [];

  // Everything, until somebody says otherwise. Publishing all of it is the usual
  // answer and the one the button used to give with no dialog at all; staging is
  // for the times it is not.
  useEffect(() => {
    if (data != null && staged == null) {
      setStaged(new Set(uncommitted.map((f) => f.path)));
    }
  }, [data]);

  const chosen = staged ?? new Set<string>();
  const all = chosen.size === uncommitted.length && uncommitted.length > 0;
  // Nothing to commit is still something to publish when commits are already
  // ahead of test — an interrupted publish leaves exactly that.
  const publishable = chosen.size > 0 || outgoing.length > 0;

  async function publish() {
    setError(null);
    setBusy(true);
    try {
      // All of them means "everything saved", which is not the same list: it is
      // the one the server builds itself, and it excludes the half-written
      // `.saving` files a crashed save leaves behind.
      await api.pushToTest(message.trim(), all ? undefined : [...chosen]);
      onPublished('Published to test.');
      onClose();
    } catch (e) {
      setError((e as Error).message);
      // A jobs file may have been broken between opening this and pressing the
      // button. Re-read, so the list below says which rather than only the count.
      reload();
      setBusy(false);
    }
  }

  function toggle(path: string) {
    setStaged((current) => {
      const next = new Set(current ?? []);
      next.has(path) ? next.delete(path) : next.add(path);
      return next;
    });
  }

  return (
    <Modal
      title="Publish to test"
      wide
      onClose={onClose}
      footer={(
        <>
          <Button
            disabled={busy || data == null || problems.length > 0 || !publishable}
            onClick={publish}
          >
            <Upload className="size-3.5" aria-hidden="true" />
            {busy
              ? 'Publishing…'
              : chosen.size > 0
                ? `Publish ${chosen.size} file(s) to test`
                : `Publish ${outgoing.length} commit(s) to test`}
          </Button>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
        </>
      )}
    >
      <ErrorBanner error={error ?? loadError} />

      {data == null ? (
        <p className="text-base text-muted-foreground">Reading your branch…</p>
      ) : (
        <>
          {problems.length > 0 && (
            <section>
              <h3 className="mb-1 flex items-center gap-1.5 text-base font-semibold text-status-danger">
                <CircleAlert className="size-4 shrink-0" aria-hidden="true" />
                {problems.length === 1
                  ? 'One jobs file will not parse'
                  : `${problems.length} jobs files will not parse`}
              </h3>
              <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
                A broken <code className="font-mono text-code">*.jobs.yaml</code> on your own
                branch is a file mid-edit. The same file in test is a job the scheduler will not
                run and nobody will notice — so publishing stops here. These block whether or not
                you stage them: the whole branch moves to test, not only what you tick.
              </p>
              <ul className="flex flex-col gap-2">
                {problems.map((file) => (
                  <li key={file.path}
                    className="rounded-lg border border-status-danger/40 bg-card px-3 py-2">
                    <Link
                      to={editPath(projectSlug(), 'mine', file.path, 'source')}
                      onClick={onClose}
                      className="font-mono text-code text-primary hover:underline"
                    >
                      {file.path}
                    </Link>
                    <ul className="mt-1 flex flex-col gap-0.5">
                      {file.problems.map((problem, i) => (
                        <li key={i} className="flex items-baseline gap-2 text-base">
                          <span className="w-[6rem] shrink-0 text-right font-mono text-xs text-muted-subtle">
                            line {problem.line}
                          </span>
                          <span>{problem.message}</span>
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section>
            <div className="mb-1 flex items-baseline gap-3">
              <h3 className="text-base font-semibold">
                Saved on your branch, not yet in test
              </h3>
              {uncommitted.length > 0 && (
                <button
                  type="button"
                  className="text-base text-primary hover:underline"
                  onClick={() => setStaged(all
                    ? new Set<string>()
                    : new Set(uncommitted.map((f) => f.path)))}
                >
                  {all ? 'Clear all' : 'Select all'}
                </button>
              )}
            </div>
            {uncommitted.length === 0 ? (
              <p className="text-base text-muted-foreground">
                Nothing saved that is not already committed.
              </p>
            ) : (
              <>
                <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
                  Ticked files become one commit under your message. Anything left
                  unticked stays on your branch, still saved, to publish later.
                </p>
                <ul className="flex flex-col gap-0.5 rounded-lg border border-border bg-card px-3 py-2">
                  {uncommitted.map((file) => (
                    <li key={file.path} className="flex items-center gap-2">
                      <input
                        type="checkbox"
                        id={`stage-${file.path}`}
                        checked={chosen.has(file.path)}
                        onChange={() => toggle(file.path)}
                        className="size-3.5 shrink-0 accent-[var(--primary)]"
                      />
                      <label htmlFor={`stage-${file.path}`} className="min-w-0 flex-1 cursor-pointer">
                        <ul><FileChange status={file.status} path={file.path} /></ul>
                      </label>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </section>

          {outgoing.length > 0 && (
            <section>
              <h3 className="mb-1 text-base font-semibold">
                Already committed here, going too
              </h3>
              <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
                Test fast-forwards onto your branch, so every commit already on it travels —
                these are not something to stage or leave behind.
              </p>
              <CommitList commits={outgoing} />
            </section>
          )}

          <label className="flex flex-col gap-1">
            <span className="text-base font-semibold">Message</span>
            <input
              autoFocus
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder="What did you change?"
              aria-label="Publish message"
              className="h-8 w-full max-w-[42rem] rounded-md border border-input bg-background px-2 text-base text-foreground outline-none focus:border-ring"
            />
          </label>
        </>
      )}
    </Modal>
  );
}
