import { describe, expect, it } from 'vitest';
import { promotionProgress } from './promotionSteps';

/** The states of the journey that prompted this, in the order they were hit. */
const clean = { ahead: 0, dirty: false, hasBranch: true };
const at = (steps: { label: string; state: string }[], label: string) =>
  steps.find((s) => s.label.startsWith(label))!.state;

describe('promotionProgress', () => {
  it('sends a brand-new notebook to push first, not to a refusal', () => {
    // What the server says here is "Nothing to promote: 'etl.nb.md' exists in
    // neither environment" — true, and no help to somebody looking at that file.
    const { steps } = promotionProgress({
      reasons: ["Nothing to promote: 'etl.nb.md' exists in neither environment."],
      standing: { ahead: 1, dirty: true, hasBranch: true },
      isAdmin: true,
      eligible: false,
    });
    expect(at(steps, 'Push')).toBe('current');
    expect(at(steps, 'Add a job')).toBe('todo');
    expect(at(steps, 'Run')).toBe('todo');
    expect(steps[0].detail).toContain('committed there');
  });

  it('moves to the job once the notebook is on test', () => {
    const { steps } = promotionProgress({
      reasons: ['No jobs are defined for this notebook in test — nothing proves it works.'],
      standing: clean,
      isAdmin: true,
      eligible: false,
    });
    expect(at(steps, 'Push')).toBe('done');
    expect(at(steps, 'Add a job')).toBe('current');
    expect(at(steps, 'Run')).toBe('todo');
  });

  it('keeps a refusal it cannot map, rather than paraphrasing it away', () => {
    const real = 'nightly has no green run of this content in test.';
    const { steps } = promotionProgress({
      reasons: [real], standing: clean, isAdmin: true, eligible: false,
    });
    expect(at(steps, 'Run')).toBe('current');
    expect(steps.find((s) => s.label.startsWith('Run'))!.detail).toBe(real);
  });

  it('ends on promote when the gate is met', () => {
    const { steps, warning } = promotionProgress({
      reasons: [], standing: clean, isAdmin: true, eligible: true,
    });
    expect(steps.map((s) => s.state)).toEqual(['done', 'done', 'done', 'current']);
    expect(warning).toBeUndefined();
  });

  it('says a member is not the one who can press it', () => {
    const { steps } = promotionProgress({
      reasons: [], standing: clean, isAdmin: false, eligible: true,
    });
    expect(at(steps, 'Promote')).toBe('todo');
    expect(steps[3].detail).toContain('project admin');
  });

  it('warns when promoting would ship something other than what is on screen', () => {
    // Promote acts on what is committed on test. Eligible plus unpushed work is
    // the one case where the button is green and wrong.
    const { warning, steps } = promotionProgress({
      reasons: [], standing: { ahead: 2, dirty: false, hasBranch: true },
      isAdmin: true, eligible: true,
    });
    expect(warning).toContain('not on test');
    expect(steps[0].detail).toContain('not in the promotion');
  });

  it('does not add the warning to a notebook that is blocked anyway', () => {
    const { warning } = promotionProgress({
      reasons: ['No jobs are defined for this notebook in test.'],
      standing: { ahead: 3, dirty: true }, isAdmin: true, eligible: false,
    });
    expect(warning).toBeUndefined();
  });
});
