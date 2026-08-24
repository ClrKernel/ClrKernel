import { Search } from 'lucide-react';
import { Fragment } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { breadcrumbFor } from '../breadcrumb';
import { showsSearch, withQuery } from '../search';
import type { AccentName } from '../theme/palette';
import { AccentPicker } from './AccentPicker';
import { EnvBadge } from './common';

/**
 * A context strip, not a toolbar. It says where you are and lets you filter
 * what is in front of you; the page says what you can do there. Nothing else
 * earns a place here — which is why the API key field lives in Settings, where
 * it belongs as configuration.
 */
export function TopBar({
  accent,
  onAccent,
}: {
  accent: AccentName;
  onAccent: (accent: AccentName) => void;
}) {
  const location = useLocation();
  const navigate = useNavigate();
  const crumbs = breadcrumbFor(location.pathname, location.search);
  const query = new URLSearchParams(location.search).get('q') ?? '';

  return (
    <header className="flex h-12.5 shrink-0 items-center border-b border-border bg-card px-4">
      <nav aria-label="Breadcrumb" className="flex min-w-0 flex-1 items-center gap-2">
        <Link to="/" className="shrink-0 font-semibold text-foreground hover:no-underline">
          ClrKernel Jobs
        </Link>
        {crumbs.map((crumb, index) => (
          <Fragment key={`${crumb.label}-${index}`}>
            <span aria-hidden="true" className="shrink-0 text-status-idle">
              /
            </span>
            {crumb.to ? (
              <Link
                to={crumb.to}
                className="shrink-0 rounded-sm text-muted-foreground outline-none hover:text-foreground hover:no-underline focus-visible:ring-2 focus-visible:ring-ring"
              >
                {crumb.label}
              </Link>
            ) : (
              <span
                className="truncate font-semibold text-foreground"
                // The untruncated value, so a shortened notebook path is still
                // readable without opening it.
                title={crumb.full ?? crumb.label}
                aria-current="page"
              >
                {crumb.label}
              </span>
            )}
            {crumb.badge && <EnvBadge env={crumb.badge} />}
          </Fragment>
        ))}
      </nav>

      <div className="flex shrink-0 items-center gap-2">
        {showsSearch(location.pathname) && (
          <label className="flex w-[230px] items-center gap-2 rounded-lg border border-border bg-background px-2.5 py-1 text-muted-subtle focus-within:border-ring">
            <Search className="size-[14px] shrink-0" aria-hidden="true" />
            <input
              type="search"
              value={query}
              // `replace`, not `push`: typing eight characters should not put
              // eight entries between you and the page you came from.
              onChange={(e) =>
                navigate(
                  { pathname: location.pathname, search: withQuery(location.search, e.target.value) },
                  { replace: true },
                )
              }
              placeholder="Search jobs…"
              aria-label="Search"
              className="w-full min-w-0 border-none bg-transparent p-0 text-base text-foreground outline-none placeholder:text-muted-subtle"
            />
          </label>
        )}
        <AccentPicker accent={accent} onAccent={onAccent} />
      </div>
    </header>
  );
}
