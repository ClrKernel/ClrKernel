import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { api, type Job, type Run } from '../api';
import { EnvBadge, ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { matchesQuery } from '../search';
import { useCanWrite } from '../sessionContext';

export function Jobs() {
  const navigate = useNavigate();
  const canWrite = useCanWrite();
  const { data, error } = usePolling<{ jobs: Job[]; errors: string[] }>(() => api.jobs(), 5000);
  const { data: health } = usePolling(() => api.health(), null);
  // The catalog does not carry a last-run status, so it comes from the run
  // list: newest first, so the first sighting of a job is its latest run.
  // Slower than the catalog poll on purpose — this is 200 runs on the wire for
  // one pill per row, and a pill that is fifteen seconds stale costs nothing.
  const { data: runs } = usePolling<Run[]>(() => api.runs(200), 15000);
  const lastRun = new Map<string, string>();
  for (const run of runs ?? []) {
    const key = `${run.environment}/${run.jobName}`;
    if (!lastRun.has(key)) {
      lastRun.set(key, run.status);
    }
  }
  const query = new URLSearchParams(useLocation().search).get('q') ?? '';
  const all = data?.jobs ?? [];
  const jobs = all.filter((job) =>
    matchesQuery(query, job.name, job.environment, job.notebook, job.cron, ...job.dependsOn),
  );
  const problems = data?.errors ?? [];
  // New jobs are created in test when the git workflow is on; prod is promote-only.
  const editableEnv = health?.gitEnabled ? 'test' : 'default';

  return (
    <div>
      <PageHeader title="Jobs">
        {canWrite && (
          <Button asChild size="sm">
            <Link to={`/jobs/${editableEnv}/new`}>New job</Link>
          </Button>
        )}
      </PageHeader>
      <ErrorBanner error={error} />
      {problems.length > 0 && (
        <Alert variant="warning" className="mb-4">
          <AlertTitle>Catalog problems</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {problems.map((problem) => (
                <li key={problem}>{problem}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}

      {jobs.length === 0 ? (
        <p className="text-base text-muted-foreground">
          {all.length > 0 ? (
            <>No jobs match “{query}”.</>
          ) : (
            <>
              No jobs yet. Add one from the{' '}
              <Link className="text-primary hover:underline" to="/notebooks">
                Notebooks
              </Link>{' '}
              tab.
            </>
          )}
        </p>
      ) : (
        <div className="table-box">
          <table className="table">
          <thead>
            <tr>
              <th>Job</th>
              <th>Notebook</th>
              <th>Schedule</th>
              <th>Depends on</th>
              <th>Last run</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map((job) => (
              <tr
                key={`${job.environment}/${job.name}`}
                className="cursor-pointer"
                onClick={() =>
                  navigate(`/jobs/${job.environment}/${encodeURIComponent(job.name)}`)
                }
              >
                <td className="whitespace-nowrap">
                  <span className="font-semibold text-primary">{job.name}</span>
                  {job.environment !== 'default' && (
                    <EnvBadge env={job.environment} className="ml-1.5" />
                  )}
                  {!job.enabled && (
                    <Badge variant="outline" className="ml-1.5 font-normal">
                      disabled
                    </Badge>
                  )}
                </td>
                <td className="font-mono text-code text-muted-foreground">{job.notebook}</td>
                <td className="font-mono text-code text-muted-foreground">
                  {job.cron || (job.dependsOn.length > 0 ? `after ${job.dependsOn[0]}` : 'manual')}
                </td>
                <td className="text-muted-foreground">{job.dependsOn.join(', ') || '—'}</td>
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
      )}
    </div>
  );
}
