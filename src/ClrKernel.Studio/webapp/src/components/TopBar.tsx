import { ChevronDown, Search, Settings2 } from 'lucide-react';
import { Fragment, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Input } from '@/components/ui/input';
import { api, type BranchSummary } from '../api';
import { breadcrumbFor } from '../breadcrumb';
import { usePolling } from './common';
import { useProjects } from '../projectContext';
import { editPath, pathFromSplat, sectionOf, switchProject, type NotebookView } from '../routes';
import { showsSearch, withQuery } from '../search';
import type { AccentName, ThemeName } from '../theme/palette';
import type { ThemeMode } from '../theme/theme';
import { AccentPicker } from './AccentPicker';
import { ThemePicker } from './ThemePicker';
import { EnvBadge } from './common';

/**
 * Which project everything below is about, as the root of the breadcrumb.
 *
 * It belongs here rather than on the pages: the rail is icon-only and has
 * nowhere to put it, and a project is not something you do — it is where you
 * are, which is what this strip says.
 *
 * Picking one navigates. It used to only set the selection, which left you on a
 * job or a file belonging to the project you had just left, with the address bar
 * still naming it — so the only way to open something in another project was to
 * go out to a list first. It goes to the section rather than to the same page:
 * this project's `nightly` is not that project's, and the other project's list
 * is the honest answer to "show me that one instead".
 */
function ProjectSwitcher() {
  const { projects, current, select } = useProjects();
  const navigate = useNavigate();
  const location = useLocation();
  const project = projects.find((p) => p.slug === current);
  const [filter, setFilter] = useState('');

  function open(slug: string) {
    select(slug);
    const to = switchProject(location.pathname, slug);
    if (to != null) {
      navigate(to);
    }
  }

  const needle = filter.trim().toLowerCase();
  // The slug as well as the name: a repo is as likely to be remembered by the
  // folder it is checked out in as by whatever it was titled.
  const shown = projects.filter(
    (p) => needle === '' || p.name.toLowerCase().includes(needle) || p.slug.includes(needle),
  );

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="flex shrink-0 items-center gap-1 rounded-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
          aria-label={`Project: ${project?.name ?? current}`}
        >
          {project?.name ?? current}
          <ChevronDown className="size-3.5 shrink-0" aria-hidden="true" />
        </button>
      </DropdownMenuTrigger>
      {/* Wide enough for a project name, and capped so a long one truncates
          rather than wrapping every item onto two lines. */}
      <DropdownMenuContent align="start" className="max-h-[60vh] w-[min(20rem,80vw)] overflow-y-auto">
        {/* Shown once there are enough to hunt through. A plain div, not a menu
            item: an item takes focus as the pointer crosses it, which would move
            the caret out of the box mid-word. And the keydown stops here, because
            Radix's menus implement type-ahead by listening for it — without this
            every letter jumps the highlight instead of reaching the input. */}
        {projects.length > 7 && (
          <div className="px-1.5 pb-1.5">
            <Input
              autoFocus
              value={filter}
              placeholder="Filter projects…"
              aria-label="Filter projects"
              className="h-7 text-xs"
              onChange={(e) => setFilter(e.target.value)}
              // A menu implements type-ahead by listening for keydown, so every
              // letter typed here would jump the highlight instead of reaching
              // the box. Stopped — except Escape and Tab, which are the menu's.
              //
              // The arrows have to be handed over by hand: Radix moves between
              // items with a roving focus group that listens on the items
              // themselves, so an arrow pressed in a box that is not one reaches
              // nothing at all. Focus the first item (or the last) and the group
              // takes it from there.
              onKeyDown={(e) => {
                if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                  const items = e.currentTarget
                    .closest('[role="menu"]')
                    ?.querySelectorAll<HTMLElement>('[role="menuitem"]');
                  if (items?.length) {
                    e.preventDefault();
                    (e.key === 'ArrowDown' ? items[0] : items[items.length - 1]).focus();
                  }
                  return;
                }
                if (e.key !== 'Escape' && e.key !== 'Tab') {
                  e.stopPropagation();
                }
              }}
            />
          </div>
        )}
        {shown.map((p) => (
          <DropdownMenuItem
            key={p.slug}
            onSelect={() => open(p.slug)}
            // A menu item focuses itself as the pointer crosses it. With a filter
            // box above the list that takes the caret out of it mid-word, and the
            // next letter goes to the menu's type-ahead instead of the box —
            // which is a strange thing to have happen while you are still typing.
            // Refused here; `hover:` below is what draws the highlight instead,
            // and the arrow keys still focus items the ordinary way.
            onPointerMove={(e) => e.preventDefault()}
            onPointerLeave={(e) => e.preventDefault()}
            className={`hover:bg-accent hover:text-accent-foreground ${
              p.slug === current ? 'font-medium text-foreground' : ''}`}
          >
            <span className="w-3 shrink-0" aria-hidden="true">
              {p.slug === current ? '✓' : ''}
            </span>
            <span className="truncate">{p.name}</span>
          </DropdownMenuItem>
        ))}
        {shown.length === 0 && (
          <p className="px-2 py-1.5 text-xs text-muted-subtle">Nothing matches “{filter}”.</p>
        )}
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link to="/settings/projects" className="hover:no-underline">
            <Settings2 className="size-3.5" aria-hidden="true" />
            Manage projects…
          </Link>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * Which branch the open notebook is being read from.
 *
 * Beside the file name rather than in the page toolbar: the toolbar is what you
 * can *do* here, and which branch you are on is part of what you are looking at.
 * Everything but your own is read-only, which the list says rather than leaving
 * you to infer it from a name.
 */
function BranchSwitcher({ project, branch: current, path, view }: {
  project: string;
  branch: string;
  path: string;
  /** Kept across the switch: reading somebody else's diff should stay the diff. */
  view: NotebookView;
}) {
  const navigate = useNavigate();
  const { data } = usePolling(() => api.branches(), null);
  const branches: BranchSummary[] = data?.branches ?? [];
  const here = branches.find((b) => b.id === current);

  function open(branch: BranchSummary) {
    navigate(editPath(project, branch.id, path, view));
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={`Branch: ${here?.label ?? current}`}
          className="inline-flex shrink-0 items-center gap-1 rounded-full border border-border bg-surface-panel px-2 py-px text-xs font-semibold text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
        >
          {here?.mine ? 'mine' : here?.label ?? current}
          <ChevronDown className="size-3" aria-hidden="true" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start">
        {branches.filter((b) => b.mine).map((b) => (
          <DropdownMenuItem key={b.id} onSelect={() => open(b)}>
            <span className="w-3 shrink-0" aria-hidden="true">{b.id === current ? '✓' : ''}</span>
            {b.label}
          </DropdownMenuItem>
        ))}
        {branches.some((b) => !b.mine) && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuLabel className="text-xs font-normal text-muted-subtle">
              Read-only
            </DropdownMenuLabel>
          </>
        )}
        {branches.filter((b) => !b.mine).map((b) => (
          <DropdownMenuItem key={b.id} onSelect={() => open(b)}>
            <span className="w-3 shrink-0" aria-hidden="true">{b.id === current ? '✓' : ''}</span>
            {b.label}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * A context strip, not a toolbar. It says where you are and lets you filter
 * what is in front of you; the page says what you can do there. Nothing else
 * earns a place here — which is why the API key field lives in Settings, where
 * it belongs as configuration.
 */
export function TopBar({
  accent,
  onAccent,
  mode,
  theme,
  onMode,
}: {
  accent: AccentName;
  onAccent: (accent: AccentName) => void;
  mode: ThemeMode;
  theme: ThemeName;
  onMode: (mode: ThemeMode) => void;
}) {
  const location = useLocation();
  const navigate = useNavigate();
  const crumbs = breadcrumbFor(location.pathname);
  const query = new URLSearchParams(location.search).get('q') ?? '';
  // /jobs/:project/… and /files/:project/edit/:branch/*path — the trail is built
  // from the path, so the pieces the switchers need come from it too.
  const segments = location.pathname.split('/').filter(Boolean);
  const inProject = sectionOf(location.pathname) != null;

  return (
    <header className="flex h-12.5 shrink-0 items-center border-b border-border bg-card px-4">
      <nav aria-label="Breadcrumb" className="flex min-w-0 flex-1 items-center gap-2">
        <Link to="/" className="shrink-0 font-semibold text-foreground hover:no-underline">
          ClrKernel Studio
        </Link>
        {/* Only where a project is what the page is about. The dashboard is the
            whole server, and Channels is server-wide: a selector there would be a
            control with nothing to change. Settings is mostly server-wide too —
            its one project-scoped tab, Secrets, carries a picker of its own
            rather than making this one appear for the other six. */}
        {inProject && (
          <>
            <span aria-hidden="true" className="shrink-0 text-status-idle">
              /
            </span>
            <ProjectSwitcher />
          </>
        )}
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
            {/* On the editor the badge is the branch, and the branch is a place
                you can move to — so it is the switcher rather than a label. */}
            {crumb.badge === 'branch' ? (
              <BranchSwitcher
                project={segments[1]}
                branch={segments[3]}
                path={pathFromSplat(segments.slice(4).join('/'))}
                view={segments[2] as NotebookView}
              />
            ) : (
              crumb.badge && <EnvBadge env={crumb.badge} />
            )}
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
        <ThemePicker mode={mode} theme={theme} onMode={onMode} />
        <AccentPicker accent={accent} onAccent={onAccent} />
      </div>
    </header>
  );
}
