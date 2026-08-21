import { Link, useLocation, useNavigate } from 'react-router-dom';
import { api, isActive, type Run, type Stats } from '../api';
import { EnvBadge, ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { duration, timeAgo } from '../ipynb';
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
                    to={`/jobs/${run.environment}/${encodeURIComponent(run.jobName)}`}
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

export function Dashboard() {
  const { data, error } = usePolling<{ stats: Stats; runs: Run[] }>(
    async () => ({ stats: await api.stats(7), runs: await api.runs(25) }),
    3000,
  );
  const query = new URLSearchParams(useLocation().search).get('q') ?? '';

  const stats = data?.stats;
  const runs = data?.runs ?? [];
  const rate = stats && stats.total > 0 ? Math.round((stats.succeeded / stats.total) * 100) : null;
  const running = runs.filter((r) => isActive(r.status)).length;
  const shown = runs.filter((run) =>
    matchesQuery(query, run.jobName, run.environment, run.notebookPath, run.status, run.trigger),
  );

  return (
    <div>
      <PageHeader title="Dashboard" />
      <ErrorBanner error={error} />

      {/* The cards state the health of the whole install, so they do not follow
          the filter — only the table below does. */}
      <div className="mb-5 grid max-w-[820px] grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard value={`${stats?.total ?? '—'}`} label="runs · 7 days" />
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
          value={`${running}`}
          label="in flight"
          tone={running ? 'text-status-running' : undefined}
        />
      </div>

      <h2 className="mb-1.5 text-lg font-semibold">Recent runs</h2>
      {query && shown.length === 0 ? (
        <p className="text-base text-muted-foreground">No runs match “{query}”.</p>
      ) : (
        <RunTable runs={shown} showNotebook />
      )}
    </div>
  );
}
