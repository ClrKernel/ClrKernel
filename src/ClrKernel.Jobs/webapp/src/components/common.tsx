import { CircleAlert } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { SelectGroup, SelectItem, SelectLabel, SelectSeparator } from '@/components/ui/select';
import type { BranchTree, CellStatus, RunStatus } from '../api';

/** A branch that belongs to a person rather than to the project. */
export function isPersonalBranch(name: string): boolean {
  return name.startsWith('user-');
}

/**
 * The branch list inside a Select: your own and the two that run, then everybody
 * else's under a heading that says what they are.
 *
 * Reading another person's branch is allowed and writing to it is not — for
 * everyone, admins included — so the grouping is the whole explanation and the
 * page needs none.
 */
export function BranchOptions({ branches }: { branches: BranchTree[] }) {
  const ours = branches.filter((b) => !isPersonalBranch(b.name));
  const theirs = branches.filter((b) => isPersonalBranch(b.name));
  return (
    <>
      <SelectGroup>
        {ours.map((branch) => (
          <SelectItem key={branch.name} value={branch.name}>
            {branch.label}
          </SelectItem>
        ))}
      </SelectGroup>
      {/* A group, not a bare heading: Radix throws outright if a SelectLabel has
          no SelectGroup around it, which takes the whole app down — and only once
          somebody else has a branch, so it would never show up on a server with
          one person on it. */}
      {theirs.length > 0 && (
        <SelectGroup>
          <SelectSeparator />
          <SelectLabel className="text-xs font-normal text-muted-subtle">Read-only</SelectLabel>
          {theirs.map((branch) => (
            <SelectItem key={branch.name} value={branch.name}>
              {branch.label}
            </SelectItem>
          ))}
        </SelectGroup>
      )}
    </>
  );
}

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

/**
 * The branch chip: a tinted pill — `test` amber, `prod` green, your own plain.
 *
 * Semantic rather than accent-derived on purpose — production is production
 * whichever accent the user picked, the same rule the ANSI palette follows.
 * Anything that is not one of the two known environments falls back to the
 * neutral outline, so a custom env name still renders as a chip.
 */
const ENV_CHIP: Record<string, string> = {
  test: 'bg-env-test-bg text-env-test border-env-test-border',
  // Your own branch is neither of the two things that run; it reads as chrome
  // rather than as a third environment, because it is not one.
  mine: 'bg-surface-panel text-muted-foreground border-border',
  prod: 'bg-env-prod-bg text-env-prod border-env-prod-border',
};

export function EnvBadge({ env, className = '' }: { env: string; className?: string }) {
  const tint = ENV_CHIP[env.toLowerCase()] ?? 'bg-transparent text-muted-subtle border-border';
  return (
    <span
      className={`inline-flex shrink-0 items-center rounded-full border px-2 py-px text-xs font-semibold ${tint} ${className}`}
    >
      {env}
    </span>
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
        <h1 className="text-xl font-bold tracking-tight">{title}</h1>
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
