import { Settings as SettingsIcon } from 'lucide-react';
import { Fragment } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { breadcrumbFor } from '../breadcrumb';
import type { AccentName } from '../theme/palette';
import { AccentPicker } from './AccentPicker';

/**
 * A context strip, not a toolbar. It says where you are; the page says what you
 * can do there. Nothing else earns a place here — which is why the API key
 * field moved to Settings, where it belongs as configuration.
 */
export function TopBar({
  accent,
  onAccent,
}: {
  accent: AccentName;
  onAccent: (accent: AccentName) => void;
}) {
  const location = useLocation();
  const crumbs = breadcrumbFor(location.pathname, location.search);

  return (
    <header className="flex h-12 shrink-0 items-center gap-2 border-b border-border bg-background px-4">
      <nav aria-label="Breadcrumb" className="flex min-w-0 flex-1 items-center gap-1.5 text-sm">
        {crumbs.map((crumb, index) => (
          <Fragment key={`${crumb.label}-${index}`}>
            {index > 0 && (
              <span aria-hidden="true" className="text-muted-foreground/60">
                ›
              </span>
            )}
            {crumb.to ? (
              <Link
                to={crumb.to}
                className="rounded-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
              >
                {crumb.label}
              </Link>
            ) : (
              <span
                className="truncate font-medium text-foreground"
                // The untruncated value, so a shortened notebook path is still
                // readable without opening it.
                title={crumb.full ?? crumb.label}
                aria-current="page"
              >
                {crumb.label}
              </span>
            )}
            {crumb.badge && (
              <Badge variant="secondary" className="font-mono text-[11px]">
                {crumb.badge}
              </Badge>
            )}
          </Fragment>
        ))}
      </nav>

      <div className="flex shrink-0 items-center gap-1">
        <AccentPicker accent={accent} onAccent={onAccent} />
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon" className="size-8" asChild>
              <Link to="/settings" aria-label="Settings">
                <SettingsIcon className="size-4" aria-hidden="true" />
              </Link>
            </Button>
          </TooltipTrigger>
          <TooltipContent>Settings</TooltipContent>
        </Tooltip>
      </div>
    </header>
  );
}
