import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Badge } from '@/components/ui/badge';
import { api, isActive, type Job, type Run, type Stats } from '../api';
import { EnvBadge, ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { duration, timeAgo } from '../ipynb';
import { useProjects } from '../projectContext';
import { jobPath, jobsPath } from '../routes';
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
                    to={`/jobs/${run.project}/${run.environment}/${encodeURIComponent(run.jobName)}`}
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
 * Every project's jobs, grouped.
 *
 * The dashboard is the one page that is about the whole server rather than about
 * one project, so this is where the shape of the install is legible: which
 * projects there are, what each one runs, and what happened last time. Every row
 * goes to that job in its own project — the project is in the path, so the link
 * means one job and not "whichever project happens to be selected".
 */
function JobsByProject({ jobs, lastRun }: { jobs: Job[]; lastRun: Map<string, string> }) {
  const { projects } = useProjects();
  const navigate = useNavigate();
  const [shut, setShut] = useState<Set<string>>(new Set());

  // In the registry's order, so the list does not reshuffle as jobs come and go.
  // A project with nothing in it is still worth a line: "no jobs yet" is an
  // answer, and leaving it out looks like the project is missing.
  const groups = projects.map((project) => ({
    project,
    jobs: jobs.filter((job) => job.project === project.slug),
  }));

  if (groups.length === 0) {
    return null;
  }

  return (
    <div className="mb-5">
      {groups.map(({ project, jobs: theirs }) => {
        const open = !shut.has(project.slug);
        return (
          <div key={project.slug} className="mb-3">
            <div className="mb-1.5 flex items-center gap-2">
              <button
                type="button"
                onClick={() => setShut((current) => {
                  const next = new Set(current);
                  next.has(project.slug) ? next.delete(project.slug) : next.add(project.slug);
                  return next;
                })}
                aria-expanded={open}
                className="flex items-center gap-1.5 rounded-sm text-lg font-semibold outline-none hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
              >
                <span aria-hidden="true" className="w-3 text-[10px] text-muted-subtle">
                  {open ? '▾' : '▸'}
                </span>
                {project.name}
              </button>
              <span className="text-base text-muted-subtle">
                {theirs.length === 1 ? '1 job' : `${theirs.length} jobs`}
              </span>
              <Link
                className="text-base text-primary hover:underline"
                to={jobsPath(project.slug)}
              >
                open
              </Link>
            </div>
            {open && (theirs.length === 0 ? (
              <p className="pl-5 text-base text-muted-foreground">No jobs yet.</p>
            ) : (
              <div className="table-box">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Job</th>
                      <th>Notebook</th>
                      <th>Schedule</th>
                      <th>Last run</th>
                    </tr>
                  </thead>
                  <tbody>
                    {theirs.map((job) => (
                      <tr
                        key={`${job.environment}/${job.name}`}
                        className="cursor-pointer"
                        onClick={() => navigate(jobPath(job.project, job.environment, job.name))}
                      >
                        <td className="whitespace-nowrap">
                          <span className="font-semibold text-primary">{job.name}</span>
                          {job.environment !== 'default' && (
                            <EnvBadge env={job.environment} className="ml-1.5" />
                          )}
                          {/* Yours and not yet anybody's: it is on this list
                              because you wrote it, not because it runs. */}
                          {job.environment === 'mine' && (
                            <Badge variant="outline" className="ml-1.5 font-normal">
                              not pushed
                            </Badge>
                          )}
                          {!job.enabled && (
                            <Badge variant="outline" className="ml-1.5 font-normal">disabled</Badge>
                          )}
                        </td>
                        <td className="font-mono text-code text-muted-foreground">{job.notebook}</td>
                        <td className="font-mono text-code text-muted-foreground">
                          {job.cron || (job.dependsOn.length > 0 ? `after ${job.dependsOn[0]}` : 'manual')}
                        </td>
                        <td>
                          {(() => {
                            const status = lastRun.get(`${job.environment}/${job.name}`);
                            return status ? <StatusBadge status={status} /> : '—';
                          })()}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}

export function Dashboard() {
  const { data, error } = usePolling<{ stats: Stats; runs: Run[]; jobs: Job[] }>(
    async () => ({
      stats: await api.stats(7),
      runs: await api.runs(25),
      jobs: (await api.jobs()).jobs,
    }),
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
  const jobs = (data?.jobs ?? []).filter((job) =>
    matchesQuery(query, job.name, job.environment, job.notebook, job.cron, ...job.dependsOn),
  );
  // The last run of each job, newest first, so the first sighting is the latest.
  const lastRun = new Map<string, string>();
  for (const run of runs) {
    const key = `${run.environment}/${run.jobName}`;
    if (!lastRun.has(key)) {
      lastRun.set(key, run.status);
    }
  }

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

      <JobsByProject jobs={jobs} lastRun={lastRun} />

      <h2 className="mb-1.5 text-lg font-semibold">Recent runs</h2>
      {query && shown.length === 0 ? (
        <p className="text-base text-muted-foreground">No runs match “{query}”.</p>
      ) : (
        <RunTable runs={shown} showNotebook />
      )}
    </div>
  );
}
