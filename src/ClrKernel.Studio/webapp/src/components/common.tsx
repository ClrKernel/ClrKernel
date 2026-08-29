import { ChevronRight, CircleAlert } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { SelectGroup, SelectItem, SelectLabel, SelectSeparator } from '@/components/ui/select';
import type { ApiLanguage, BranchTree, CellStatus, RunStatus } from '../api';
import { languageGroups, languageOptions, runsOnProvider } from '../notebook';

/**
 * The reveal handle a collapsed sidebar leaves behind.
 *
 * Top-aligned rather than centred on the column. It is the same control as the
 * ⟨ that hid the panel, so it belongs where that button was: the eye goes back
 * to where it last clicked. Centred, it sat wherever the window happened to be
 * tall — which is nowhere in particular, and a long way from the header on a
 * big screen.
 *
 * And drawn as a chip rather than a bare glyph. A 12px chevron in the faintest
 * text colour on a 16px strip is not visibly a control; the border and the
 * lighter fill are what say "press me", and they are the same border the button
 * that collapsed the panel wears.
 *
 * Shared because there were two of these — one lucide chevron, one `⟩`
 * character, different sizes, different weights: the same control looking like
 * two different things depending on which sidebar you had shut.
 */
export function CollapsedRail({ label, onExpand }: { label: string; onExpand: () => void }) {
  return (
    <button
      type="button"
      onClick={onExpand}
      title={label}
      aria-label={label}
      className="flex w-[22px] shrink-0 items-start justify-center border-r border-border bg-muted pt-2 text-muted-foreground outline-none hover:bg-surface-panel-strong hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
    >
      <span className="flex size-[18px] items-center justify-center rounded-sm border border-input bg-background">
        <ChevronRight className="size-3.5" aria-hidden="true" />
      </span>
    </button>
  );
}

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
/**
 * The cell language picker's contents: what the kernel offers, grouped the way
 * the kernel says to group it.
 *
 * Two rules from the spec, both about where information belongs. The dropdown
 * carries each language's provider list as secondary text — that is the answer
 * to "can this cell run on my connection", and it is only wanted while choosing.
 * The <em>button</em> shows the display name alone, which is why the trigger
 * renders its own label rather than letting the selected item's markup mirror
 * into it: a cell footer reading "T-SQL SqlServer · Odbc · Jdbc" would spend a
 * line of every cell on something you read once.
 */
export function LanguageOptions({
  languages, providerType,
}: {
  languages: ApiLanguage[];
  /** The `$type` of the connection this notebook queries, when it names one.
   *  Options that cannot run on it are marked rather than hidden: a language you
   *  cannot see teaches nothing about why. */
  providerType?: string | null;
}) {
  const groups = languageGroups(languages);
  return (
    <>
      {groups.map((group, index) => (
        // A group, not a bare heading: Radix throws outright if a SelectLabel has
        // no SelectGroup around it — the same trap BranchOptions documents above.
        <SelectGroup key={group.label ?? '\u0000ungrouped'}>
          {group.label != null && (
            <>
              {index > 0 && <SelectSeparator />}
              <SelectLabel className="text-xs font-normal text-muted-subtle">
                {group.label}
              </SelectLabel>
            </>
          )}
          {group.options.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              <span className="flex flex-col items-start gap-0.5">
                <span>{option.label}</span>
                {option.detail != null && (
                  <span
                    className={`font-mono text-xs ${
                      runsOnProvider(option.value, providerType, languages)
                        ? 'text-muted-subtle'
                        : 'text-status-warning'
                    }`}
                  >
                    {option.detail}
                    {!runsOnProvider(option.value, providerType, languages)
                      && ` — not ${providerType}`}
                  </span>
                )}
              </span>
            </SelectItem>
          ))}
        </SelectGroup>
      ))}
    </>
  );
}

/** What the picker's button says: the display name, and never the provider list. */
export function languageLabelFor(value: string, languages: ApiLanguage[]): string {
  return languageOptions(languages).find((o) => o.value === value)?.label ?? value;
}

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
