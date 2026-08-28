import type { ReactNode } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { api, isActive, type Run, type Stats, type UpcomingRun } from '../api';
import { EnvBadge, ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { TabNav } from '../components/TabNav';
import { duration, timeAgo, timeUntil } from '../ipynb';
import { useProjects } from '../projectContext';
import { jobRunsPath, jobsFilePath } from '../routes';
import { matchesQuery } from '../search';

/**
 * The run table, shared with the job detail page.
 *
 * Whole rows navigate — a "details" link in the last column is a second target
 * for what the row already means. The job name stays a link of its own because
 * it goes somewhere else.
 */
export function RunTable({
  runs,
  showJob = true,
  showNotebook = false,
}: {
  runs: Run[];
  showJob?: boolean;
  showNotebook?: boolean;
}) {
  const navigate = useNavigate();
  if (runs.length === 0) {
    return <p className="text-base text-muted-foreground">No runs yet.</p>;
  }
  return (
    <div className="table-box">
      <table className="table">
        <thead>
          <tr>
            <th>Status</th>
            {showJob && <th>Job</th>}
            {showNotebook && <th>Notebook</th>}
            <th>Trigger</th>
            <th>Started</th>
            <th>Took</th>
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => (
            <tr
              key={run.id}
              className="cursor-pointer"
              onClick={() => navigate(`/runs/${run.id}`)}
            >
              <td>
                <StatusBadge status={run.status} />
              </td>
              {showJob && (
                <td className="whitespace-nowrap">
                  <Link
                    className="font-semibold text-primary hover:underline"
                    to={jobRunsPath(run.project, run.environment, run.jobName)}
                    onClick={(e) => e.stopPropagation()}
                  >
                    {run.jobName}
                  </Link>
                  {run.environment !== 'default' && (
                    <EnvBadge env={run.environment} className="ml-1.5" />
                  )}
                </td>
              )}
              {showNotebook && (
                <td className="font-mono text-code text-muted-foreground">{run.notebookPath}</td>
              )}
              <td className="text-muted-foreground">{run.trigger}</td>
              <td className="whitespace-nowrap text-muted-foreground">
                {timeAgo(run.startedAt ?? run.createdAt)}
              </td>
              <td className="whitespace-nowrap text-muted-foreground">
                {duration(run.startedAt, run.finishedAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * One number and its label. Border, no shadow — hierarchy is carried by the
 * border and by the card sitting a shade above the cream canvas.
 */
function StatCard({ value, label, tone }: { value: string; label: string; tone?: string }) {
  return (
    <div className="rounded-2xl border border-border bg-card px-3.5 py-3">
      <div className={`text-[22px] font-bold leading-tight ${tone ?? ''}`}>{value}</div>
      <div className="mt-0.5 text-xs text-muted-subtle">{label}</div>
    </div>
  );
}

/**
 * The Dashboard's views, as routes.
 *
 * Overview answers "is everything alright"; Monitoring is the grid you go to
 * when it isn't. Separate paths rather than component state, so a filtered grid
 * is a link and the back button does what it looks like it does.
 *
 * Notifications is the third: the rules that decide when something is sent, and
 * the feed of what actually went out. Channels stays on the rail, because a
 * destination is configured once and a rule is decided per project.
 */
export function DashboardTabs() {
  return (
    <TabNav
      label="Dashboard views"
      className="mb-4"
      items={[
        { to: '/', label: 'Overview' },
        { to: '/monitoring', label: 'Monitoring' },
        { to: '/notifications', label: 'Notifications' },
      ]}
    />
  );
}

/** A heading with the link that opens the same thing in full. */
function Section({
  title, to, more, children,
}: {
  title: string;
  to: string;
  more: string;
  children: ReactNode;
}) {
  return (
    <section className="mb-5">
      <div className="mb-1.5 flex items-baseline justify-between gap-3">
        <h2 className="text-lg font-semibold">{title}</h2>
        <Link className="text-base text-primary hover:underline" to={to}>{more}</Link>
      </div>
      {children}
    </section>
  );
}

/**
 * How each project has been doing, as one bar per project.
 *
 * A bar rather than a percentage on its own: 100% of two runs and 100% of two
 * thousand are the same number and not the same fact, and the width says which
 * you are looking at. Only projects that ran something appear — a row of zeroes
 * for one nobody scheduled is noise.
 */
function ProjectHealth({ stats, days }: { stats: Stats | undefined; days: number }) {
  const { projects } = useProjects();
  const rows = stats?.byProject ?? [];
  if (rows.length === 0) {
    return (
      <p className="text-base text-muted-foreground">
        Nothing has run in the last {days} days.
      </p>
    );
  }
  const busiest = Math.max(...rows.map((r) => r.total));
  return (
    <div className="flex flex-col gap-2">
      {rows.map((row) => {
        const rate = row.total > 0 ? Math.round((row.succeeded / row.total) * 100) : 0;
        const name = projects.find((p) => p.slug === row.project)?.name ?? row.project;
        return (
          <Link
            key={row.project}
            to={`/monitoring?project=${encodeURIComponent(row.project)}`}
            className="flex items-center gap-3 rounded-xl border border-border bg-card px-3 py-2 hover:no-underline hover:border-primary"
          >
            <span className="w-40 shrink-0 truncate font-semibold">{name}</span>
            <span className="flex h-2 flex-1 overflow-hidden rounded-full bg-muted"
                  style={{ maxWidth: `${Math.max(8, (row.total / busiest) * 100)}%` }}>
              <span className="bg-status-success" style={{ width: `${rate}%` }} />
              <span className="flex-1 bg-status-error" />
            </span>
            <span className="w-28 shrink-0 text-right text-base text-muted-foreground">
              {rate}% of {row.total}
            </span>
          </Link>
        );
      })}
    </div>
  );
}

/** What the crons say happens next. */
function Upcoming({ runs }: { runs: UpcomingRun[] }) {
  const navigate = useNavigate();
  if (runs.length === 0) {
    return <p className="text-base text-muted-foreground">Nothing is scheduled.</p>;
  }
  return (
    <div className="table-box">
      <table className="table">
        <tbody>
          {runs.map((run) => (
            <tr
              key={`${run.project}/${run.environment}/${run.job}`}
              className="cursor-pointer"
              onClick={() => navigate(jobsFilePath(run.project, run.environment, run.jobsFile))}
            >
              <td className="whitespace-nowrap font-semibold">{run.job}</td>
              <td><EnvBadge env={run.environment} /></td>
              <td className="font-mono text-code text-muted-foreground">{run.cron}</td>
              <td className="whitespace-nowrap text-right text-muted-foreground">
                {timeUntil(run.at)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const WINDOW_DAYS = 7;

/**
 * Is everything alright?
 *
 * Four questions and a link out of each: what is running now, what broke, what
 * happens next, and how each project has been doing. It deliberately does not
 * reproduce the monitoring grid — every section here is a handful of rows and a
 * way to see the rest of them where the filtering and the paging actually live.
 */
export function Dashboard() {
  const { data, error } = usePolling<{
    stats: Stats;
    runs: Run[];
    failures: Run[];
    upcoming: UpcomingRun[];
  }>(
    async () => ({
      stats: await api.stats(WINDOW_DAYS),
      runs: (await api.runs(25)).runs,
      failures: (await api.runGrid('status=Failed&limit=5')).runs,
      upcoming: (await api.upcoming(5)).upcoming,
    }),
    5000,
  );
  const query = new URLSearchParams(useLocation().search).get('q') ?? '';

  const stats = data?.stats;
  const runs = data?.runs ?? [];
  const rate = stats && stats.total > 0 ? Math.round((stats.succeeded / stats.total) * 100) : null;
  const live = runs.filter((r) => isActive(r.status));
  const shown = live.filter((run) =>
    matchesQuery(query, run.jobName, run.environment, run.notebookPath, run.status, run.trigger),
  );

  return (
    <div>
      <PageHeader title="Dashboard" />
      <DashboardTabs />
      <ErrorBanner error={error} />

      {/* The cards state the health of the whole install, so they do not follow
          the filter — only the table below does. */}
      <div className="mb-5 grid max-w-[820px] grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard value={`${stats?.total ?? '—'}`} label={`runs · ${WINDOW_DAYS} days`} />
        <StatCard
          value={rate == null ? '—' : `${rate}%`}
          label="success rate"
          tone="text-status-success"
        />
        <StatCard
          value={`${stats?.failed ?? '—'}`}
          label="failed"
          tone={stats?.failed ? 'text-status-error' : undefined}
        />
        <StatCard
          value={`${live.length}`}
          label="in flight"
          tone={live.length ? 'text-status-running' : undefined}
        />
      </div>

      <Section title="Running now" to="/monitoring?status=Running" more="in the grid">
        {live.length === 0 ? (
          <p className="text-base text-muted-foreground">Nothing is running.</p>
        ) : query && shown.length === 0 ? (
          <p className="text-base text-muted-foreground">Nothing running matches “{query}”.</p>
        ) : (
          <RunTable runs={shown} showNotebook />
        )}
      </Section>

      <Section title="Recent failures" to="/monitoring?status=Failed" more="all failures">
        {(data?.failures ?? []).length === 0 ? (
          <p className="text-base text-muted-foreground">
            Nothing has failed{stats?.total ? ` in the last ${WINDOW_DAYS} days` : ''}.
          </p>
        ) : (
          <RunTable runs={data!.failures} showNotebook />
        )}
      </Section>

      <Section title="Up next" to="/files" more="the files that schedule them">
        <Upcoming runs={data?.upcoming ?? []} />
      </Section>

      <Section title="By project" to="/monitoring" more="every run">
        <ProjectHealth stats={stats} days={WINDOW_DAYS} />
      </Section>
    </div>
  );
}
