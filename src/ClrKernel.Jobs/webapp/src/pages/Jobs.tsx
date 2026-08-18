import { Link } from 'react-router-dom';
import { api, type Job } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

export function Jobs() {
  const { data, error } = usePolling<{ jobs: Job[]; errors: string[] }>(() => api.jobs(), 5000);
  const jobs = data?.jobs ?? [];
  const problems = data?.errors ?? [];

  return (
    <div>
      <div className="row-between">
        <h1>Jobs</h1>
        <Link className="button" to="/jobs/new">
          New job
        </Link>
      </div>
      <ErrorBanner error={error} />
      {problems.length > 0 && (
        <div className="banner banner-warn">
          <strong>Catalog problems</strong>
          <ul>
            {problems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        </div>
      )}

      {jobs.length === 0 ? (
        <p className="muted">
          No jobs yet. Add one from the <Link to="/notebooks">Notebooks</Link> tab.
        </p>
      ) : (
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
              <tr key={job.name}>
                <td>
                  <Link to={`/jobs/${encodeURIComponent(job.name)}`}>{job.name}</Link>
                  {!job.enabled && <span className="chip chip-muted">disabled</span>}
                </td>
                <td className="muted">{job.notebook}</td>
                <td className="muted">{job.cron ? <code>{job.cron}</code> : 'manual'}</td>
                <td className="muted">{job.dependsOn.join(', ') || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
