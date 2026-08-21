import { Link } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { api, isActive, type Run, type Stats } from '../api';
import { ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { duration, timeAgo } from '../ipynb';

export function RunTable({ runs, showJob = true }: { runs: Run[]; showJob?: boolean }) {
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
            <th>Trigger</th>
            <th>Started</th>
            <th>Took</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => (
            <tr key={run.id}>
              <td>
                <StatusBadge status={run.status} />
              </td>
              {showJob && (
                <td>
                  <Link
                    className="text-primary hover:underline"
                    to={`/jobs/${run.environment}/${encodeURIComponent(run.jobName)}`}
                  >
                    {run.jobName}
                  </Link>
                  {run.environment !== 'default' && (
                    <Badge variant="secondary" className="ml-2 font-mono text-xs">
                      {run.environment}
                    </Badge>
                  )}
                </td>
              )}
              <td className="text-muted-foreground">{run.trigger}</td>
              <td className="text-muted-foreground">
                {timeAgo(run.startedAt ?? run.createdAt)}
              </td>
              <td className="text-muted-foreground">
                {duration(run.startedAt, run.finishedAt)}
              </td>
              <td>
                <Link className="text-primary hover:underline" to={`/runs/${run.id}`}>
                  details
                </Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * One number and its label. Hairline border, no shadow — the white surface on
 * the near-white page is what separates it.
 */
function StatCard({ value, label, tone }: { value: string; label: string; tone?: string }) {
  return (
    <div className="rounded-md border border-border bg-card px-3 py-2.5 shadow-[var(--shadow-card)]">
      <div className={`font-mono text-xl leading-tight ${tone ?? ''}`}>{value}</div>
      <div className="mt-0.5 text-base text-muted-foreground">{label}</div>
    </div>
  );
}

export function Dashboard() {
  const { data, error } = usePolling<{ stats: Stats; runs: Run[] }>(
    async () => ({ stats: await api.stats(7), runs: await api.runs(25) }),
    3000,
  );

  const stats = data?.stats;
  const runs = data?.runs ?? [];
  const rate = stats && stats.total > 0 ? Math.round((stats.succeeded / stats.total) * 100) : null;
  const running = runs.filter((r) => isActive(r.status)).length;

  return (
    <div>
      <PageHeader title="Dashboard" />
      <ErrorBanner error={error} />

      <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard value={`${stats?.total ?? '—'}`} label="runs (7 days)" />
        <StatCard value={rate == null ? '—' : `${rate}%`} label="success rate" />
        <StatCard
          value={`${stats?.failed ?? '—'}`}
          label="failed"
          // The only coloured number on the page, and only when it is non-zero.
          tone={stats?.failed ? 'text-status-error' : undefined}
        />
        <StatCard value={`${running}`} label="in flight" />
      </div>

      <h2 className="mb-2 text-base font-semibold">Recent runs</h2>
      <RunTable runs={runs} />
    </div>
  );
}
