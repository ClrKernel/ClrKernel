import { LayoutDashboard, ListChecks, NotebookText, Radio, type LucideIcon } from 'lucide-react';
import { Link, useMatch } from 'react-router-dom';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';

/**
 * The activity bar. Icons only, always — it does not expand or collapse, so
 * there is one width to lay out against and no state to persist.
 *
 * Settings is deliberately absent: it lives in the top bar, and two entry
 * points to one page is exactly the kind of busyness this redesign removes.
 */
const NAV: { to: string; label: string; icon: LucideIcon; end: boolean }[] = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard, end: true },
  { to: '/jobs', label: 'Jobs', icon: ListChecks, end: false },
  { to: '/notebooks', label: 'Notebooks', icon: NotebookText, end: false },
  { to: '/channels', label: 'Channels', icon: Radio, end: false },
];

/**
 * `useMatch` rather than `NavLink`'s render props: `asChild` wraps the trigger
 * in a Radix `Slot`, which merges `className` by joining strings — so a
 * `NavLink` className *function* passed through it lands on the element as its
 * own source text and none of the classes apply.
 */
function RailLink({ to, label, icon: Icon, end }: (typeof NAV)[number]) {
  const active = useMatch({ path: to, end }) != null;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Link
          to={to}
          aria-label={label}
          aria-current={active ? 'page' : undefined}
          className={[
            // `relative` anchors the active bar, which sits flush to the rail's
            // left edge rather than inside the item's own padding.
            'relative flex size-[40px] items-center justify-center rounded-md outline-none transition-colors',
            'focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-surface-rail',
            active
              ? 'bg-accent text-primary'
              : 'text-muted-foreground hover:bg-accent hover:text-foreground',
          ].join(' ')}
        >
          {active && (
            <span
              aria-hidden="true"
              className="absolute -left-1 top-1/2 h-5 w-0.5 -translate-y-1/2 rounded-r-sm bg-primary"
            />
          )}
          <Icon className="size-[20px]" aria-hidden="true" />
        </Link>
      </TooltipTrigger>
      <TooltipContent side="right">{label}</TooltipContent>
    </Tooltip>
  );
}

export function Rail() {
  return (
    <nav
      aria-label="Sections"
      className="flex w-full flex-col items-center gap-1 border-r border-border bg-surface-rail py-2"
    >
      <Tooltip>
        <TooltipTrigger asChild>
          <Link
            to="/"
            aria-label="ClrKernel Jobs — go to dashboard"
            className="mb-1 flex size-[32px] items-center justify-center rounded-md bg-primary text-primary-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-surface-rail"
          >
            {/* The wordmark does not fit a 48px rail, so the mark stands in. */}
            <svg viewBox="0 0 16 16" className="size-[20px]" aria-hidden="true">
              <path
                d="M4.5 5 7.5 8l-3 3M9 11h3.5"
                stroke="currentColor"
                strokeWidth="2"
                fill="none"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          </Link>
        </TooltipTrigger>
        <TooltipContent side="right">ClrKernel Jobs</TooltipContent>
      </Tooltip>

      {NAV.map((item) => (
        <RailLink key={item.to} {...item} />
      ))}
    </nav>
  );
}
