import { useCallback, useEffect, useRef, useState } from 'react';
import { AUTOSAVE_DELAY, statusOf, type SaveStatus } from './autosave';

/**
 * Writes the editor's buffer to your branch while you work.
 *
 * On a debounce after you stop typing, and again at the moments where losing the
 * last second would be the thing you noticed: leaving the tab, and navigating
 * away. The caller flushes before running a cell, too — see `flush`.
 *
 * One write at a time. An edit that arrives mid-write is not dropped: the pass
 * that finishes schedules another, so the last thing you typed always reaches
 * disk, and two writes never race for the same file.
 */
export function useAutosave(
  /**
   * Something that changes on every edit — the buffer itself will do.
   *
   * The debounce restarts from this rather than from `dirty`: `dirty` goes true
   * on the first keystroke and stays true, so a timer keyed on it fires once,
   * eight hundred milliseconds into a sentence, writes the two letters that were
   * there, and never runs again.
   */
  revision: unknown,
  dirty: boolean,
  write: (keepalive?: boolean) => Promise<void>,
  onWritten?: () => void,
): { status: SaveStatus; flush: () => Promise<void>; retry: () => void } {
  const [writing, setWriting] = useState(false);
  const [failed, setFailed] = useState(false);

  // Refs, not state: the timer callback and the unmount cleanup both need the
  // *current* values, and a closure over state would hold whatever they were
  // when the effect last ran.
  const latest = useRef({ dirty, write, onWritten });
  latest.current = { dirty, write, onWritten };
  const inFlight = useRef<Promise<void> | null>(null);

  const flush = useCallback(async (keepalive = false) => {
    // Queue behind a write already going out rather than starting a second one:
    // two PUTs to one path arriving out of order would persist the older buffer.
    while (inFlight.current) {
      await inFlight.current.catch(() => undefined);
    }
    if (!latest.current.dirty) {
      return;
    }
    setWriting(true);
    const attempt = latest.current.write(keepalive);
    inFlight.current = attempt;
    try {
      await attempt;
      setFailed(false);
      latest.current.onWritten?.();
    } catch {
      // Held until the next write succeeds. A failure that clears itself is a
      // failure you find out about by losing the file.
      setFailed(true);
    } finally {
      inFlight.current = null;
      setWriting(false);
    }
  }, []);

  useEffect(() => {
    if (!dirty) {
      return;
    }
    const timer = setTimeout(() => void flush(), AUTOSAVE_DELAY);
    return () => clearTimeout(timer);
  }, [revision, dirty, flush]);

  // Leaving the tab, and leaving the page. `visibilitychange` is the one that
  // actually fires on a phone or on a closed laptop lid; `beforeunload` cannot
  // await anything, so it only warns.
  useEffect(() => {
    // keepalive, because this is the one call that has to outlive the page. An
    // ordinary fetch is cancelled the moment the document goes away, which is
    // exactly when this fires.
    const onHidden = () => {
      if (document.visibilityState === 'hidden') {
        void flush(true);
      }
    };
    const onUnload = (event: BeforeUnloadEvent) => {
      if (latest.current.dirty) {
        event.preventDefault();
      }
    };
    document.addEventListener('visibilitychange', onHidden);
    window.addEventListener('beforeunload', onUnload);
    return () => {
      document.removeEventListener('visibilitychange', onHidden);
      window.removeEventListener('beforeunload', onUnload);
    };
  }, [flush]);

  // Navigating away inside the app: the component goes, the write still has to.
  // keepalive here too — "away" is sometimes a full page load, and telling the
  // two apart at this point is not worth getting wrong.
  useEffect(() => () => void flush(true), [flush]);

  // ⌘S / Ctrl+S writes now rather than in eight hundred milliseconds. It is not
  // needed, and people will press it anyway — so it had better not be the
  // browser's save-page dialog.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        void flush();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [flush]);

  return {
    status: statusOf(dirty, writing, failed),
    flush,
    retry: () => {
      setFailed(false);
      void flush();
    },
  };
}
