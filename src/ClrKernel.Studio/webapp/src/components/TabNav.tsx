import { Link, useLocation } from 'react-router-dom';

export interface TabRoute {
  /** Where the tab goes. Also what decides whether it is the active one. */
  to: string;
  label: string;
}

/**
 * Tabs that are *routes*, not component state.
 *
 * The underline treatment is the one the job-detail and editor tabs already use;
 * it lives in the unlayered block at the bottom of `styles.css`, keyed off
 * `data-tabs="line"` so one rule serves both the Radix `Tabs` component and this.
 * That is why the markup carries `data-slot`/`data-state` attributes it did not
 * generate — they are the styling contract, not a claim to be a Radix widget.
 *
 * Deliberately *not* `role="tab"`: ARIA tabs own a panel in the same document
 * and manage focus between them. These are links that change the URL, so they
 * are navigation, and `aria-current` is the right way to say which one you are
 * on. A screen reader announcing "tab 2 of 4" for something that navigates is a
 * worse experience than the plain truth.
 */
export function TabNav({
  items,
  label,
  className = '',
}: {
  items: TabRoute[];
  /** Names the group for assistive tech: "Settings sections", "Job views". */
  label: string;
  className?: string;
}) {
  const { pathname } = useLocation();

  return (
    <nav aria-label={label} data-tabs="line" className={className}>
      {items.map((item) => {
        // Exact match, or a child route beneath it — so /settings/git stays
        // active on a hypothetical /settings/git/advanced.
        const active = pathname === item.to || pathname.startsWith(`${item.to}/`);
        return (
          <Link
            key={item.to}
            to={item.to}
            data-slot="tabs-trigger"
            data-state={active ? 'active' : 'inactive'}
            aria-current={active ? 'page' : undefined}
            className="outline-none hover:no-underline focus-visible:ring-2 focus-visible:ring-ring"
          >
            {item.label}
          </Link>
        );
      })}
    </nav>
  );
}
