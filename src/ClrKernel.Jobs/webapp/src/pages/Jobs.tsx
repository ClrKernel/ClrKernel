import { Link } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { api, type Job } from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';

export function Jobs() {
  const { data, error } = usePolling<{ jobs: Job[]; errors: string[] }>(() => api.jobs(), 5000);
  const { data: health } = usePolling(() => api.health(), null);
  const jobs = data?.jobs ?? [];
  const problems = data?.errors ?? [];
  // New jobs are created in dev when the git workflow is on; prod is promote-only.
  const editableEnv = health?.gitEnabled ? 'dev' : 'default';

  return (
    <div>
      <PageHeader title="Jobs">
        <Button asChild variant="outline" size="sm">
          <Link to={`/jobs/${editableEnv}/new`}>New job</Link>
        </Button>
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
        <p className="text-sm text-muted-foreground">
          No jobs yet. Add one from the{' '}
          <Link className="text-primary hover:underline" to="/notebooks">
            Notebooks
          </Link>{' '}
          tab.
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
            </tr>
          </thead>
          <tbody>
            {jobs.map((job) => (
              <tr key={`${job.environment}/${job.name}`}>
                <td>
                  <Link
                    className="text-primary hover:underline"
                    to={`/jobs/${job.environment}/${encodeURIComponent(job.name)}`}
                  >
                    {job.name}
                  </Link>
                  {job.environment !== 'default' && (
                    <Badge variant="secondary" className="ml-2 font-mono text-[11px]">
                      {job.environment}
                    </Badge>
                  )}
                  {!job.enabled && (
                    <Badge variant="outline" className="ml-2 font-normal">
                      disabled
                    </Badge>
                  )}
                </td>
                <td className="font-mono text-muted-foreground">{job.notebook}</td>
                <td className="text-muted-foreground">
                  {job.cron ? <code className="font-mono">{job.cron}</code> : 'manual'}
                </td>
                <td className="text-muted-foreground">{job.dependsOn.join(', ') || '—'}</td>
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
