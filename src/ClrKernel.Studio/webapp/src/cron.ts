/**
 * The two questions a cron field cannot answer for itself: which of the five
 * fields the caret is in, and what expression the wizard's choices add up to.
 *
 * React-free and tested. Building a cron is exactly the sort of thing that looks
 * obvious and is wrong at the edges — a weekly schedule with no days picked, a
 * "every 90 minutes" that cron cannot express — and getting it wrong writes a
 * schedule somebody trusts.
 *
 * Validation is emphatically not here. The server previews every expression with
 * the same Cronos the scheduler runs, so this only has to *produce*; whether the
 * result is runnable is answered by the thing that will run it.
 */

/** The five fields, in order, as the help line names them. */
export const CRON_FIELDS = [
  'minute', 'hour', 'day-of-month', 'month', 'day-of-week',
] as const;

/**
 * Which field a caret at `offset` sits in, or null when the expression has no
 * field there — past the end of a five-field expression, or in leading space.
 *
 * A caret at the end of a field counts as being in it: that is where you are
 * while typing one, and the highlight going out as you finish each character
 * would be worse than no highlight.
 */
export function fieldAt(expression: string, offset: number): number | null {
  const caret = Math.max(0, Math.min(offset, expression.length));
  let index = -1;
  let inField = false;
  for (let i = 0; i < expression.length; i++) {
    const space = /\s/.test(expression[i]);
    if (!space && !inField) {
      index += 1;
      inField = true;
    } else if (space) {
      inField = false;
      // A caret in the space *after* a field still belongs to it, until the next
      // one starts — otherwise the highlight blinks off between words.
      if (i >= caret) {
        break;
      }
    }
    if (i >= caret) {
      break;
    }
  }
  if (index < 0 || index >= CRON_FIELDS.length) {
    return null;
  }
  return index;
}

/** What the wizard is being asked for. */
export type CronSpec =
  | { every: 'minutes'; minutes: number }
  | { every: 'hour'; minute: number }
  | { every: 'day'; hour: number; minute: number }
  | { every: 'week'; hour: number; minute: number; days: number[] }
  | { every: 'month'; hour: number; minute: number; dayOfMonth: number };

/** Sunday-first, matching cron's 0-6 and every calendar anyone has seen. */
export const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

export const DEFAULT_SPEC: CronSpec = { every: 'day', hour: 2, minute: 0 };

/**
 * The expression for a set of choices.
 *
 * Ranges are collapsed (`1-5` rather than `1,2,3,4,5`) because that is what
 * somebody reading the field afterwards would have written, and the field is
 * read far more often than the wizard is opened.
 */
export function buildCron(spec: CronSpec): string {
  switch (spec.every) {
    case 'minutes':
      // `*/1` is every minute, which `*` says more plainly.
      return spec.minutes <= 1 ? '* * * * *' : `*/${spec.minutes} * * * *`;
    case 'hour':
      return `${spec.minute} * * * *`;
    case 'day':
      return `${spec.minute} ${spec.hour} * * *`;
    case 'week':
      // No days chosen is not "never" in cron, it is a syntax error — so it means
      // every day, which is what an unticked week most nearly is.
      return `${spec.minute} ${spec.hour} * * ${
        spec.days.length === 0 ? '*' : collapse(spec.days)}`;
    case 'month':
      return `${spec.minute} ${spec.hour} ${spec.dayOfMonth} * *`;
  }
}

/** `[1,2,3,5]` → `1-3,5`. */
function collapse(days: number[]): string {
  const sorted = [...new Set(days)].sort((a, b) => a - b);
  const parts: string[] = [];
  let start = sorted[0];
  let previous = sorted[0];
  for (const day of sorted.slice(1)) {
    if (day === previous + 1) {
      previous = day;
      continue;
    }
    parts.push(range(start, previous));
    start = day;
    previous = day;
  }
  parts.push(range(start, previous));
  return parts.join(',');
}

const range = (from: number, to: number) =>
  from === to ? String(from) : to === from + 1 ? `${from},${to}` : `${from}-${to}`;

/**
 * The choices behind an expression, when it is one the wizard could have made.
 *
 * Best-effort and deliberately narrow: it exists so that opening the wizard on a
 * schedule you already have starts from that schedule rather than from a default
 * that silently discards it. Anything hand-written beyond this shape gets the
 * default, and the wizard says it will replace what is there.
 */
export function specOf(expression: string): CronSpec | null {
  const fields = expression.trim().split(/\s+/);
  if (fields.length !== 5) {
    return null;
  }
  const [minute, hour, dayOfMonth, month, dayOfWeek] = fields;
  if (month !== '*') {
    return null;
  }
  const everyN = /^\*\/(\d+)$/.exec(minute);
  if (everyN && hour === '*' && dayOfMonth === '*' && dayOfWeek === '*') {
    return { every: 'minutes', minutes: Number(everyN[1]) };
  }
  if (!/^\d+$/.test(minute)) {
    return null;
  }
  if (hour === '*' && dayOfMonth === '*' && dayOfWeek === '*') {
    return { every: 'hour', minute: Number(minute) };
  }
  if (!/^\d+$/.test(hour)) {
    return null;
  }
  if (dayOfMonth === '*' && dayOfWeek === '*') {
    return { every: 'day', hour: Number(hour), minute: Number(minute) };
  }
  if (dayOfMonth === '*') {
    const days = expand(dayOfWeek);
    return days ? { every: 'week', hour: Number(hour), minute: Number(minute), days } : null;
  }
  if (dayOfWeek === '*' && /^\d+$/.test(dayOfMonth)) {
    return {
      every: 'month', hour: Number(hour), minute: Number(minute), dayOfMonth: Number(dayOfMonth),
    };
  }
  return null;
}

/** `1-3,5` → `[1,2,3,5]`, or null for anything less ordinary. */
function expand(field: string): number[] | null {
  const days: number[] = [];
  for (const part of field.split(',')) {
    const span = /^(\d)-(\d)$/.exec(part);
    if (span) {
      for (let day = Number(span[1]); day <= Number(span[2]); day++) {
        days.push(day);
      }
    } else if (/^\d$/.test(part)) {
      days.push(Number(part));
    } else {
      return null;
    }
  }
  return days.length ? days : null;
}
