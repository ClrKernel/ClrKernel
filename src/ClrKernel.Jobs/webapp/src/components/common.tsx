import { useCallback, useEffect, useRef, useState } from 'react';
import type { CellStatus, RunStatus } from '../api';

export function StatusBadge({ status }: { status: RunStatus | CellStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>;
}

export function ErrorBanner({ error }: { error: string | null }) {
  return error ? <div className="banner banner-error">{error}</div> : null;
}

/**
 * Fetches on mount and then on an interval, so views stay live without a
 * websocket. `active` lets a page poll fast while a run is in flight and stop
 * once it settles.
 */
export function usePolling<T>(
  load: () => Promise<T>,
  intervalMs: number | null,
  deps: unknown[] = [],
): { data: T | null; error: string | null; reload: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadRef = useRef(load);
  loadRef.current = load;

  const reload = useCallback(() => {
    let cancelled = false;
    loadRef
      .current()
      .then((value) => {
        if (!cancelled) {
          setData(value);
          setError(null);
        }
      })
      .catch((e: Error) => {
        if (!cancelled) {
          setError(e.message);
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const cancel = reload();
    if (intervalMs == null) {
      return cancel;
    }
    const timer = setInterval(reload, intervalMs);
    return () => {
      cancel();
      clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intervalMs, reload, ...deps]);

  return { data, error, reload };
}
