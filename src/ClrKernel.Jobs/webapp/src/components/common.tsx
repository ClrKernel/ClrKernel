import { CircleAlert } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import type { CellStatus, RunStatus } from '../api';

/** Which `--status-*` token a run or cell state paints with. */
const STATUS_TOKEN: Record<string, string> = {
  running: 'bg-status-running',
  succeeded: 'bg-status-success',
  failed: 'bg-status-error',
  cancelled: 'bg-status-warning',
  skipped: 'bg-status-idle',
  pending: 'bg-status-idle',
  queued: 'bg-status-idle',
};

/**
 * Status as a neutral chip with a coloured dot, not a coloured chip.
 *
 * Colour is reserved for what is actually communicating state — here, the dot.
 * A row of fully-tinted badges makes a table of runs read as a warning even
 * when every one of them succeeded.
 *
 * `status` is widened to `string` because a cell's run state carries one: the
 * dot falls back to idle for anything unrecognised rather than rendering
 * nothing, so an unknown status still shows its name.
 */
export function StatusBadge({ status }: { status: RunStatus | CellStatus | string }) {
  const key = status.toLowerCase();
  return (
    <Badge variant="secondary" className="gap-1.5 font-normal">
      <span
        aria-hidden="true"
        className={`size-1.5 shrink-0 rounded-full ${STATUS_TOKEN[key] ?? 'bg-status-idle'} ${
          key === 'running' ? 'animate-pulse' : ''
        }`}
      />
      {status}
    </Badge>
  );
}

export function ErrorBanner({ error }: { error: string | null }) {
  return error ? (
    <Alert variant="destructive" className="my-3">
      <CircleAlert aria-hidden="true" />
      <AlertDescription className="text-destructive">{error}</AlertDescription>
    </Alert>
  ) : null;
}

/**
 * A page's title row. Title on the left, page-level actions on the right — the
 * top bar says where you are, this says what you can do here.
 */
export function PageHeader({
  title,
  description,
  children,
}: {
  title: string;
  description?: React.ReactNode;
  /** Right-aligned actions. */
  children?: React.ReactNode;
}) {
  return (
    <div className="mb-4 flex items-start justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-lg font-semibold tracking-tight">{title}</h1>
        {/* Cells and tables take the full width; sentences do not. A 1600px
            line of prose is unreadable. */}
        {description && (
          <p className="mt-0.5 max-w-[78ch] text-base text-muted-foreground">{description}</p>
        )}
      </div>
      {children && <div className="flex shrink-0 items-center gap-2">{children}</div>}
    </div>
  );
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
