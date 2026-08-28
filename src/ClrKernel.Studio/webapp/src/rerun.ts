/**
 * What "run that again" is about to do, in words, before it does it.
 *
 * The wording is here rather than inline in a page because it is the last thing
 * between a click and code running in production, and the two things it must get
 * right — which branch, and how many runs — are exactly the two a page composing
 * a sentence out of local state gets wrong.
 */
import type { Run } from './api';

/** The runs a selection will actually start: one per job, newest occurrence first. */
export function rerunJobs(runs: Run[]): string[] {
  const seen = new Set<string>();
  return [...runs]
    .sort((a, b) => (b.startedAt ?? b.createdAt).localeCompare(a.startedAt ?? a.createdAt))
    .filter((run) => (seen.has(run.jobName) ? false : seen.add(run.jobName) !== undefined))
    .map((run) => run.jobName);
}

/** The branches a selection spans. More than one and the server refuses it. */
export function branchesIn(runs: Run[]): string[] {
  return [...new Set(runs.map((run) => run.environment))];
}

/**
 * The confirmation. Names the branch and the number of runs, and says which
 * version — because "rerun" at HEAD and "rerun" at the recorded commit are
 * different acts and only the sentence tells them apart.
 */
export function rerunQuestion(runs: Run[], exactVersion: boolean): string {
  const jobs = rerunJobs(runs);
  const branch = branchesIn(runs)[0] ?? '';
  const what = jobs.length === 1 ? jobs[0] : `${jobs.length} jobs`;
  const version = exactVersion
    ? 'at the commit it recorded'
    : `as ${branch} is now`;
  const dropped = runs.length - jobs.length;
  return [
    `Run ${what} again in ${branch}, ${version}?`,
    // Selecting a week of nightly failures is one job, not seven runs. Saying so
    // is the difference between a count that is true and one that is arithmetic.
    dropped > 0
      ? `\n${runs.length} runs selected, ${jobs.length} of them distinct jobs — each job runs once.`
      : '',
    branch === 'prod' ? '\nThis runs in production.' : '',
  ].join('');
}

/** What came back, said in one line for the banner. */
export function rerunOutcome(
  started: { job: string }[],
  refused: { reason: string }[],
): string {
  const head = started.length === 1
    ? `${started[0].job} started.`
    : `${started.length} runs started.`;
  return refused.length === 0
    ? head
    : `${head} ${refused.length} not started — ${refused.map((r) => r.reason).join(' ')}`;
}
