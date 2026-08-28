import { describe, expect, it } from 'vitest';
import { branchesIn, rerunJobs, rerunOutcome, rerunQuestion } from './rerun';
import type { Run } from './api';

const run = (jobName: string, environment: string, startedAt: string): Run => ({
  id: `${jobName}-${startedAt}`,
  project: 'default',
  environment,
  jobName,
  notebookPath: `${jobName}.nb.md`,
  status: 'Failed',
  trigger: 'Schedule',
  causedByRunId: null,
  attempt: 1,
  createdAt: startedAt,
  startedAt,
  finishedAt: startedAt,
  errorSummary: null,
  artifactPath: null,
  logPath: null,
  actorId: null,
  actorName: null,
  commitSha: 'abc1234',
  wasDirty: false,
  hadOverrides: false,
});

describe('what a selection actually starts', () => {
  // A job that failed nightly for a week is seven rows and one job. A count that
  // said "7 runs" would be arithmetic, not the truth.
  it('is one run per job, however many rows were picked', () => {
    const week = ['2026-03-01', '2026-03-02', '2026-03-03'].map((d) =>
      run('nightly', 'prod', `${d}T02:00:00Z`),
    );
    expect(rerunJobs(week)).toEqual(['nightly']);
    expect(rerunJobs([...week, run('hourly', 'prod', '2026-03-03T03:00:00Z')]))
      .toEqual(['hourly', 'nightly']);
  });

  it('reports every branch it spans, so the caller can refuse to guess', () => {
    expect(branchesIn([run('a', 'test', 'x'), run('b', 'prod', 'y'), run('c', 'test', 'z')]))
      .toEqual(['test', 'prod']);
  });
});

describe('the confirmation', () => {
  it('names the branch and the job', () => {
    expect(rerunQuestion([run('nightly', 'test', '2026-03-01T02:00:00Z')], false))
      .toBe('Run nightly again in test, as test is now?');
  });

  it('says when it is going back rather than forward', () => {
    expect(rerunQuestion([run('nightly', 'test', '2026-03-01T02:00:00Z')], true))
      .toContain('at the commit it recorded');
  });

  it('explains a count that is smaller than the selection', () => {
    const week = ['2026-03-01', '2026-03-02'].map((d) => run('nightly', 'test', `${d}T02:00:00Z`));
    const question = rerunQuestion(week, false);
    expect(question).toContain('Run nightly again');
    expect(question).toContain('2 runs selected, 1 of them distinct jobs');
  });

  it('says so out loud when the branch is production', () => {
    expect(rerunQuestion([run('nightly', 'prod', '2026-03-01T02:00:00Z')], false))
      .toContain('This runs in production.');
    expect(rerunQuestion([run('nightly', 'test', '2026-03-01T02:00:00Z')], false))
      .not.toContain('production');
  });
});

describe('the outcome', () => {
  it('reports the refusals rather than only the starts', () => {
    expect(rerunOutcome([{ job: 'a' }], [])).toBe('a started.');
    expect(rerunOutcome([{ job: 'a' }, { job: 'b' }], [{ reason: "'c' already has a run in flight." }]))
      .toBe("2 runs started. 1 not started — 'c' already has a run in flight.");
  });
});
