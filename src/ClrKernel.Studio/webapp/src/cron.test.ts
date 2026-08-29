import { describe, expect, it } from 'vitest';
import { CRON_FIELDS, buildCron, fieldAt, specOf, type CronSpec } from './cron';

describe('fieldAt', () => {
  const expression = '0 6 * * 1';
  //                  0 2 4 6 8

  it('says which field the caret is in', () => {
    expect(fieldAt(expression, 0)).toBe(0);
    expect(fieldAt(expression, 2)).toBe(1);
    expect(fieldAt(expression, 4)).toBe(2);
    expect(fieldAt(expression, 6)).toBe(3);
    expect(fieldAt(expression, 8)).toBe(4);
  });

  it('counts the end of a field as still being in it', () => {
    // Which is where the caret is for the whole time you are typing one. A
    // highlight that goes out as you finish each character is worse than none.
    expect(fieldAt('30 7 * * 1-5', 2)).toBe(0);
    expect(fieldAt('30 7 * * 1-5', 12)).toBe(4);
  });

  it('handles a caret past the end and an empty expression', () => {
    expect(fieldAt('', 0)).toBeNull();
    expect(fieldAt('   ', 1)).toBeNull();
    expect(fieldAt('0 6 * * 1', 99)).toBe(4);
  });

  it('has nothing to say about a sixth field', () => {
    // Five is what this server runs. A sixth is a mistake, and highlighting a
    // name for it would suggest otherwise.
    expect(fieldAt('0 6 * * 1 2026', 12)).toBeNull();
  });

  it('copes with the extra spaces people leave while editing', () => {
    expect(fieldAt('0   6 * * 1', 4)).toBe(1);
    expect(CRON_FIELDS[fieldAt('0   6 * * 1', 4)!]).toBe('hour');
  });
});

describe('buildCron', () => {
  it('builds the shapes the wizard offers', () => {
    expect(buildCron({ every: 'minutes', minutes: 15 })).toBe('*/15 * * * *');
    expect(buildCron({ every: 'hour', minute: 5 })).toBe('5 * * * *');
    expect(buildCron({ every: 'day', hour: 2, minute: 0 })).toBe('0 2 * * *');
    expect(buildCron({ every: 'month', hour: 3, minute: 0, dayOfMonth: 1 })).toBe('0 3 1 * *');
  });

  it('collapses a run of days the way somebody would have written it', () => {
    expect(buildCron({ every: 'week', hour: 7, minute: 30, days: [1, 2, 3, 4, 5] }))
      .toBe('30 7 * * 1-5');
    expect(buildCron({ every: 'week', hour: 6, minute: 0, days: [1] })).toBe('0 6 * * 1');
    expect(buildCron({ every: 'week', hour: 6, minute: 0, days: [0, 6] })).toBe('0 6 * * 0,6');
    // Two adjacent days read better as a list than as a range.
    expect(buildCron({ every: 'week', hour: 6, minute: 0, days: [2, 3] })).toBe('0 6 * * 2,3');
  });

  it('treats a week with no days ticked as every day', () => {
    // An empty day field is a syntax error, not "never" — and an unticked week
    // is most nearly every day.
    expect(buildCron({ every: 'week', hour: 6, minute: 0, days: [] })).toBe('0 6 * * *');
  });

  it('says every minute plainly rather than as a step of one', () => {
    expect(buildCron({ every: 'minutes', minutes: 1 })).toBe('* * * * *');
  });
});

describe('specOf', () => {
  const round = (spec: CronSpec) => specOf(buildCron(spec));

  it('round-trips everything the wizard can build', () => {
    const specs: CronSpec[] = [
      { every: 'minutes', minutes: 15 },
      { every: 'hour', minute: 5 },
      { every: 'day', hour: 2, minute: 0 },
      { every: 'week', hour: 7, minute: 30, days: [1, 2, 3, 4, 5] },
      { every: 'week', hour: 6, minute: 0, days: [0, 6] },
      { every: 'month', hour: 3, minute: 0, dayOfMonth: 1 },
    ];
    for (const spec of specs) {
      expect(round(spec), buildCron(spec)).toEqual(spec);
    }
  });

  it('declines anything it could not have built', () => {
    // Opening the wizard on one of these starts from the default and says it
    // will replace what is there — better than silently reinterpreting it.
    expect(specOf('0 6 1 1 *')).toBeNull();     // a specific month
    expect(specOf('0 6 * * MON')).toBeNull();   // named days
    expect(specOf('0 6-18 * * *')).toBeNull();  // an hour range
    expect(specOf('')).toBeNull();
    expect(specOf('0 6 * *')).toBeNull();       // four fields
  });
});
