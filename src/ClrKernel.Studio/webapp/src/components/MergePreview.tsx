import { ArrowDownToLine } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { api, type ApiIncoming } from '../api';
import { BranchGraph } from './BranchGraph';
import { CommitList, FileChange } from './CommitList';
import { ErrorBanner, usePolling } from './common';
import { Modal } from './Modal';

/**
 * What `Update from test` is about to do, before it does it.
 *
 * <p>It replaced a confirm() box reading "1 file(s) changed there. Anything you
 * have not committed is committed first, and anything that cannot merge cleanly
 * comes back as a conflict to fix." Every clause of that was true and the whole
 * was unreadable: it never said <em>which</em> file, or whose, or where any of it
 * was going — so "my stuff gets committed" read as "my stuff goes to test".</p>
 *
 * <p>So the three questions get three answers, in the order they are asked:
 * what is arriving, what of mine is in the way, and what happens to it. The
 * direction is drawn rather than described, because the thing people have
 * backwards is which branch moves.</p>
 */
export function MergePreview({ onClose, onMerged }: {
  onClose: () => void;
  onMerged: (message: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { data, error: loadError } = usePolling<ApiIncoming>(() => api.incoming(), null);

  async function merge() {
    setError(null);
    setBusy(true);
    try {
      const result = await api.updateFromTest();
      onMerged(result.merged
        ? 'Up to date with test.'
        : `Conflicts left in ${result.conflicts.join(', ')} — open each and fix the markers.`);
      onClose();
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  }

  const incoming = data?.incoming ?? [];
  const outgoing = data?.outgoing ?? [];
  const uncommitted = data?.uncommitted ?? [];
  const branch = data?.branch ?? 'your branch';

  return (
    <Modal
      title="Update from test"
      wide
      onClose={onClose}
      footer={(
        <>
          <Button disabled={busy || data == null || incoming.length === 0} onClick={merge}>
            <ArrowDownToLine className="size-3.5" aria-hidden="true" />
            {busy ? 'Merging…' : `Merge ${incoming.length} commit(s) into my branch`}
          </Button>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
        </>
      )}
    >
      <ErrorBanner error={error ?? loadError} />

      {data == null ? (
        <p className="text-base text-muted-foreground">Reading the two branches…</p>
      ) : incoming.length === 0 ? (
        <p className="text-base text-muted-foreground">
          Nothing to bring — test has no commits your branch has not already got.
        </p>
      ) : (
        <>
          <BranchGraph incoming={incoming} outgoing={outgoing} branch={branch} merged />

          {/* Said in words as well as drawn, because the drawing is the part that
              is easy to read the wrong way round. */}
          <p className="max-w-[78ch] text-base text-muted-foreground">
            Test's {incoming.length} commit(s) come <strong>into</strong> your branch.
            Nothing of yours goes to test — that is <strong>Push to test</strong>, and it is
            still yours to press afterwards.
          </p>

          <section>
            <h3 className="mb-2 text-base font-semibold">Arriving from test</h3>
            <CommitList commits={incoming} />
          </section>

          {uncommitted.length > 0 && (
            <section>
              <h3 className="mb-1 text-base font-semibold">
                Your uncommitted work — committed first, on your own branch
              </h3>
              <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
                Git will not merge over unsaved changes, and stashing them would hide them
                exactly when they matter. So these are committed on{' '}
                <code className="font-mono text-code">{branch}</code> as
                “work in progress before updating from test” before the merge starts. They stay
                on your branch.
              </p>
              <ul className="flex flex-col gap-0.5 rounded-lg border border-border bg-card px-3 py-2">
                {uncommitted.map((file) => (
                  <FileChange key={file.path} status={file.status} path={file.path} />
                ))}
              </ul>
            </section>
          )}

          <p className="max-w-[78ch] text-base text-muted-foreground">
            Anything that cannot merge cleanly comes back with conflict markers in the file for
            you to fix — nothing is resolved for you, and nothing is thrown away.
          </p>
        </>
      )}
    </Modal>
  );
}
