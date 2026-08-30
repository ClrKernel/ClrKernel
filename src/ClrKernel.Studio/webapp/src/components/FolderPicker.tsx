import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { api } from '../api';
import { ErrorBanner } from './common';
import { Modal } from './Modal';

/**
 * Picking the folder a project will live in, instead of typing a path from memory.
 *
 * It lists folders and nothing else — no files, no sizes, no contents. This is for
 * choosing where something goes, and a directory listing of the server is a bigger
 * thing to hand a browser than the question needs. The route behind it is server
 * admin only, the same gate as registering a project.
 *
 * A folder that already is a project is shown and refused, because the alternative
 * is finding out from the overlap error after the form has been filled in.
 */
export function FolderPicker({ start, onPick, onClose }: {
  /** Where to open. Null starts at the server's projects root, or its notebooks root. */
  start: string | null;
  onPick: (path: string) => void;
  onClose: () => void;
}) {
  const [at, setAt] = useState<string | null>(start);
  const [listing, setListing] =
    useState<Awaited<ReturnType<typeof api.serverFolders>> | null>(null);
  const [error, setError] = useState<string | null>(null);
  // A folder to create under the one on screen. The server makes it at register
  // time, so this only has to say what to call it.
  const [child, setChild] = useState('');

  useEffect(() => {
    let live = true;
    setError(null);
    api.serverFolders(at ?? undefined)
      .then((reply) => { if (live) { setListing(reply); setChild(''); } })
      // Keep the last listing on screen: replacing it with nothing turns a folder
      // you cannot read into a dead end rather than a refused step.
      .catch((e: Error) => { if (live) setError(e.message); });
    return () => { live = false; };
  }, [at]);

  const here = listing?.path ?? at ?? '';
  // The server is the authority on its own separator; this only has to join.
  const join = (parent: string, name: string) =>
    parent.endsWith('/') || parent.endsWith('\\') ? parent + name : `${parent}/${name}`;
  const chosen = child.trim() ? join(here, child.trim()) : here;

  return (
    <Modal
      title="Choose a folder"
      onClose={onClose}
      footer={
        <>
          <Button size="sm" disabled={!here} onClick={() => onPick(chosen)}>Use this folder</Button>
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        </>
      }
    >
      {error && <ErrorBanner error={error} />}

      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={!listing?.parent}
          onClick={() => setAt(listing!.parent)}
        >
          ↑ Up
        </Button>
        <code className="min-w-0 flex-1 truncate text-sm">{here}</code>
        {listing?.projectsRoot && listing.projectsRoot !== here && (
          <Button variant="outline" size="sm" onClick={() => setAt(listing.projectsRoot)}>
            Projects root
          </Button>
        )}
      </div>

      <div className="mt-2 max-h-[40vh] overflow-auto rounded-lg border border-input">
        {listing == null ? (
          <p className="p-3 text-base text-muted-foreground">Loading…</p>
        ) : listing.folders.length === 0 ? (
          <p className="p-3 text-base text-muted-foreground">
            No folders in here. Name one below to create it.
          </p>
        ) : (
          <ul>
            {listing.folders.map((folder) => (
              <li key={folder.path} className="border-b border-input last:border-b-0">
                <button
                  type="button"
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-base hover:bg-accent disabled:opacity-50"
                  disabled={folder.taken}
                  title={folder.taken ? 'A project is already registered here' : folder.path}
                  onClick={() => setAt(folder.path)}
                >
                  <span className="truncate">{folder.name}</span>
                  {folder.taken && (
                    <span className="text-xs text-muted-subtle">already a project</span>
                  )}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      <label className="mt-3 grid gap-1">
        <span className="text-sm text-muted-foreground">New folder in here (optional)</span>
        <Input
          value={child}
          onChange={(e) => setChild(e.target.value)}
          placeholder="finance"
          spellCheck={false}
        />
        <span className="text-xs text-muted-subtle">
          Created when the project is registered, not now.
        </span>
      </label>

      {/* The path in full, and wrapping — a temp directory is longer than the
          dialog, and putting it on the button pushed Cancel off the edge. */}
      <p className="mt-3 text-sm break-all text-muted-foreground">
        Use this folder: <code>{chosen}</code>
      </p>
    </Modal>
  );
}
