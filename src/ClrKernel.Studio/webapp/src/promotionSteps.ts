/**
 * Why promotion is blocked, as the steps left rather than the complaints made.
 *
 * The server's refusals are each true and none of them says what to do next.
 * "Nothing to promote: 'etl.nb.md' exists in neither environment" is correct and
 * useless to somebody looking at that file on their own branch — the answer is
 * *push it to test first*, and the sentence does not contain it. "No jobs are
 * defined for this notebook in test" is the same shape: a fact about test, given
 * to a person standing on `mine`, with the fix left as an exercise.
 *
 * So this maps them onto the sequence they belong to — push → add a job → run it
 * in test → promote — and marks where you are. Reasons that map onto no step are
 * kept verbatim under the run step: a private connection or a broken dependency
 * graph is a real refusal and paraphrasing it would lose it.
 *
 * React-free so it can be tested as a table. The rendering is a toast in
 * NotebookToolbar.
 */

export type StepState = 'done' | 'current' | 'todo';

export interface PromotionStep {
  label: string;
  state: StepState;
  /** Server text, or what to do — shown under the label. */
  detail?: string;
}

export interface PromotionProgress {
  steps: PromotionStep[];
  /**
   * Set when promotion would ship something other than what is on screen. On
   * your own branch Promote ships what is *committed on test*, which the Diff tab
   * says outright and the button never did.
   */
  warning?: string;
}

/** The server's own words for the two states that have an obvious next step. */
const NOT_IN_EITHER = 'exists in neither environment';
const NO_JOBS = 'No jobs are defined';

export function promotionProgress({ reasons, standing, isAdmin, eligible }: {
  reasons: string[];
  standing: { ahead?: number; dirty?: boolean; hasBranch?: boolean } | null;
  isAdmin: boolean;
  eligible: boolean;
}): PromotionProgress {
  const unpushed = (standing?.ahead ?? 0) > 0 || standing?.dirty === true;
  const notInTest = reasons.some((r) => r.includes(NOT_IN_EITHER));
  const noJobs = reasons.some((r) => r.includes(NO_JOBS));
  // Everything the mapping did not claim. Kept, because these are the refusals
  // that carry information nothing here could reconstruct.
  const rest = reasons.filter((r) => !r.includes(NOT_IN_EITHER) && !r.includes(NO_JOBS));

  const steps: PromotionStep[] = [];

  steps.push({
    label: 'Push to test',
    state: notInTest ? 'current' : 'done',
    detail: notInTest
      ? 'This notebook is not on test yet. Promotion ships what is committed there.'
      : unpushed
        ? 'Done — but you have changes since, and they are not in the promotion.'
        : undefined,
  });

  steps.push({
    label: 'Add a job',
    state: notInTest ? 'todo' : noJobs ? 'current' : 'done',
    detail: noJobs
      ? 'A notebook with no job has nothing to prove it works. Add one beside it.'
      : undefined,
  });

  const runBlocked = !notInTest && !noJobs && rest.length > 0;
  steps.push({
    label: 'Run the job in test',
    state: notInTest || noJobs ? 'todo' : runBlocked ? 'current' : 'done',
    detail: rest.length > 0 ? rest.join(' ') : undefined,
  });

  steps.push({
    label: 'Promote to production',
    state: eligible ? (isAdmin ? 'current' : 'todo') : 'todo',
    detail: eligible && !isAdmin
      ? 'Ready, but only a project admin can promote.'
      : undefined,
  });

  return {
    steps,
    // Only worth saying when it is actually true and promotion would otherwise
    // go ahead — on a blocked notebook it is noise beside the real blocker.
    warning: eligible && unpushed
      ? 'You have changes that are not on test. Promoting ships the last push, not what is on screen.'
      : undefined,
  };
}
