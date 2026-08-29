import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { CheckboxField, Field, SelectField } from '@/components/ui/field';
import {
  DEFAULT_SPEC, WEEKDAYS, buildCron, specOf, type CronSpec,
} from '../cron';
import { Modal } from './Modal';

/**
 * Picking a schedule without writing cron.
 *
 * The interval comes first and decides what else is asked — a weekly schedule
 * needs days and a monthly one needs a date, and showing both at once is how a
 * form ends up with four boxes that do not apply. The expression it builds is
 * always on screen, so this teaches the syntax rather than hiding it: most
 * people open it twice and then type `0 6 * * 1` themselves.
 *
 * It opens on the schedule already in the field when that is one it could have
 * built, and says so plainly when it is not — reinterpreting somebody's
 * hand-written cron into the nearest thing this understands would quietly change
 * when their job runs.
 */
export function CronWizard({
  current, onUse, onClose,
}: {
  current: string;
  onUse: (cron: string) => void;
  onClose: () => void;
}) {
  const parsed = specOf(current);
  const [spec, setSpec] = useState<CronSpec>(parsed ?? DEFAULT_SPEC);
  const built = buildCron(spec);

  // Every branch keeps the time it was set to, so switching Daily → Weekly does
  // not silently reset 07:30 to midnight.
  const hour = 'hour' in spec ? spec.hour : 2;
  const minute = 'minute' in spec ? spec.minute : 0;
  const time = `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
  const setTime = (next: string) => {
    const [h, m] = next.split(':').map(Number);
    if (!Number.isFinite(h) || !Number.isFinite(m)) {
      return;
    }
    setSpec((s) => ('hour' in s ? { ...s, hour: h, minute: m } : { ...s, minute: m }));
  };

  return (
    <Modal
      title="Schedule"
      onClose={onClose}
      footer={
        <>
          <span className="flex-1" />
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
          <Button size="sm" onClick={() => { onUse(built); onClose(); }}>Use this schedule</Button>
        </>
      }
    >

      {current.trim() !== '' && parsed == null && (
        <p className="text-base text-status-warning">
          <code className="font-mono text-code">{current}</code> is not a shape this can
          show, so it starts from a default. Using a schedule below replaces it.
        </p>
      )}

      <SelectField
        label="Repeats"
        value={spec.every}
        onChange={(every) => setSpec(startingFrom(every as CronSpec['every'], hour, minute))}
        options={[
          { value: 'minutes', label: 'Every N minutes' },
          { value: 'hour', label: 'Hourly' },
          { value: 'day', label: 'Daily' },
          { value: 'week', label: 'Weekly' },
          { value: 'month', label: 'Monthly' },
        ]}
      />

      {spec.every === 'minutes' && (
        <Field label="Every how many minutes">
          <Input
            type="number" min={1} max={59} value={spec.minutes}
            onChange={(e) => setSpec({ every: 'minutes', minutes: Number(e.target.value) || 1 })}
          />
        </Field>
      )}

      {spec.every === 'hour' && (
        <Field label="At this many minutes past the hour">
          <Input
            type="number" min={0} max={59} value={spec.minute}
            onChange={(e) => setSpec({ every: 'hour', minute: Number(e.target.value) || 0 })}
          />
        </Field>
      )}

      {(spec.every === 'day' || spec.every === 'week' || spec.every === 'month') && (
        <Field label="At (UTC)">
          <Input type="time" value={time} onChange={(e) => setTime(e.target.value)} />
        </Field>
      )}

      {spec.every === 'week' && (
        <fieldset className="flex flex-col gap-1">
          <legend className="text-sm font-medium">On these days</legend>
          <div className="flex flex-wrap gap-x-4 gap-y-2">
            {WEEKDAYS.map((label, day) => (
              <CheckboxField
                key={label}
                label={label}
                checked={spec.days.includes(day)}
                onChange={(checked) => setSpec({
                  ...spec,
                  days: checked
                    ? [...spec.days, day]
                    : spec.days.filter((d) => d !== day),
                })}
              />
            ))}
          </div>
          {spec.days.length === 0 && (
            <span className="block text-base text-muted-foreground">
              None ticked means every day — cron has no way to say "never".
            </span>
          )}
        </fieldset>
      )}

      {spec.every === 'month' && (
        <label>
          On day of the month
          <Input
            type="number" min={1} max={31} value={spec.dayOfMonth}
            onChange={(e) => setSpec({ ...spec, dayOfMonth: Number(e.target.value) || 1 })}
          />
          {spec.dayOfMonth > 28 && (
            <span className="block text-base text-status-warning">
              Months without a {spec.dayOfMonth}th are skipped, not moved.
            </span>
          )}
        </label>
      )}

      <p className="text-base text-muted-foreground">
        That is{' '}
        <code className="font-mono text-code text-foreground">{built}</code>
        {' '}— the field shows the next few runs once you use it.
      </p>

    </Modal>
  );
}

/** Switching interval keeps the time, and fills in what the new one needs. */
function startingFrom(every: CronSpec['every'], hour: number, minute: number): CronSpec {
  switch (every) {
    case 'minutes': return { every, minutes: 15 };
    case 'hour': return { every, minute };
    case 'day': return { every, hour, minute };
    case 'week': return { every, hour, minute, days: [1, 2, 3, 4, 5] };
    case 'month': return { every, hour, minute, dayOfMonth: 1 };
  }
}
