import {
  LayoutGrid,
  NotebookText,
  Bell,
  Play,
  Settings as SettingsIcon,
  type LucideIcon,
} from 'lucide-react';
import { Link, useMatch } from 'react-router-dom';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';

/**
 * The activity bar. Icons only, always — it does not expand or collapse, so
 * there is one width to lay out against and no state to persist.
 */
const NAV: { to: string; label: string; icon: LucideIcon; end: boolean }[] = [
  { to: '/', label: 'Dashboard', icon: LayoutGrid, end: true },
  { to: '/jobs', label: 'Jobs', icon: Play, end: false },
  { to: '/notebooks', label: 'Notebooks', icon: NotebookText, end: false },
  { to: '/channels', label: 'Channels', icon: Bell, end: false },
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
            // Absolute px, not `size-8`: rem tracks the browser's default font
            // size, and the design specifies its chrome in pixels. A rail that
            // is 42px on a 14px default is the bug this spells out.
            'flex size-[32px] items-center justify-center rounded-lg outline-none transition-colors',
            'focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-card',
            active
              ? 'bg-primary-soft text-primary'
              : 'text-muted-subtle hover:bg-primary-soft hover:text-primary',
          ].join(' ')}
        >
          <Icon className="size-[17px]" aria-hidden="true" strokeWidth={2} />
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
      // `w-full`, not `w-12`: the grid column is an absolute 48px, and a rem
      // width inside it paints narrower on any browser whose default font size
      // is not 16px, leaving a strip of page showing down the left edge.
      className="flex w-full flex-col items-center gap-1.5 border-r border-border bg-card py-2.5"
    >
      <Tooltip>
        <TooltipTrigger asChild>
          <Link
            to="/"
            aria-label="ClrKernel Jobs — go to dashboard"
            className="mb-1 flex size-[28px] items-center justify-center rounded-lg bg-primary font-mono text-[14px] font-semibold text-primary-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-card"
          >
            {/* The wordmark does not fit a 48px rail, so the prompt stands in. */}
            <span aria-hidden="true">&gt;_</span>
          </Link>
        </TooltipTrigger>
        <TooltipContent side="right">ClrKernel Jobs</TooltipContent>
      </Tooltip>

      {NAV.map((item) => (
        <RailLink key={item.to} {...item} />
      ))}

      <div className="flex-1" />

      <RailLink to="/settings" label="Settings" icon={SettingsIcon} end={false} />
    </nav>
  );
}
