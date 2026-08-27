import { Wand2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Input } from '@/components/ui/input';
import { api, type CronPreview } from '../api';
import { CRON_FIELDS, fieldAt } from '../cron';
import { CronWizard } from './CronWizard';

/**
 * Schedules worth having without writing any cron at all. Most jobs are one of
 * these; the field stays for the ones that are not.
 */
const PRESETS: { label: string; cron: string }[] = [
  { label: 'Manual or dependency-triggered — no schedule', cron: '' },
  { label: 'Every 15 minutes', cron: '*/15 * * * *' },
  { label: 'Every hour, on the hour', cron: '0 * * * *' },
  { label: 'Every day at 02:00', cron: '0 2 * * *' },
  { label: 'Weekdays at 07:30', cron: '30 7 * * 1-5' },
  { label: 'Every Monday at 06:00', cron: '0 6 * * 1' },
  { label: 'First of the month at 03:00', cron: '0 3 1 * *' },
];

/** `2026-08-25T02:00:00.0000000Z` → `Tue 25 Aug 02:00`. */
function when(iso: string): string {
  const at = new Date(iso);
  return at.toLocaleString('en-GB', {
    timeZone: 'UTC', weekday: 'short', day: '2-digit', month: 'short',
    hour: '2-digit', minute: '2-digit', hour12: false,
  });
}

/** `UTC+1`, or null when the reader is already on UTC and there is nothing to say. */
function localOffset(): string | null {
  // getTimezoneOffset is minutes *behind* UTC, so the sign is the other way round.
  const minutes = -new Date().getTimezoneOffset();
  if (minutes === 0) {
    return null;
  }
  const sign = minutes < 0 ? '−' : '+';
  const hours = Math.floor(Math.abs(minutes) / 60);
  const rest = Math.abs(minutes) % 60;
  return `UTC${sign}${hours}${rest ? `:${String(rest).padStart(2, '0')}` : ''}`;
}

/**
 * The schedule, with the two things a cron field normally makes you already know:
 * what the five fields are, and what this one will actually do.
 *
 * The preview comes from the server rather than a parser in here — it is the same
 * Cronos the scheduler runs and the save path validates with, so the field cannot
 * accept an expression the save will refuse, and cannot read it differently
 * either. Next occurrences rather than an English description: "at 02:00 daily"
 * still leaves *whose* 02:00 unanswered, and instants answer it by being ones.
 */
export function CronField({
  value,
  disabled,
  onChange,
}: {
  value: string;
  disabled?: boolean;
  onChange: (cron: string) => void;
}) {
  const [preview, setPreview] = useState<CronPreview | null>(null);
  const [wizard, setWizard] = useState(false);
  // Which of the five fields the caret is in, so the help line can point at the
  // one being typed. Null when the field is not focused — a highlight on a field
  // nobody is in is just a bolded word.
  const [field, setField] = useState<number | null>(null);

  useEffect(() => {
    const expression = value.trim();
    if (!expression) {
      setPreview(null);
      return;
    }
    // Debounced: this fires per keystroke otherwise, and a half-typed cron is
    // invalid nearly all the way through.
    const timer = setTimeout(() => {
      api.cronPreview(expression).then(setPreview).catch(() => setPreview(null));
    }, 350);
    return () => clearTimeout(timer);
  }, [value]);

  const offset = localOffset();

  return (
    <div className="field">
      <div className="flex items-center gap-2">
        <label htmlFor="cron">Schedule</label>
        <span className="text-base font-normal text-muted-foreground">
          (cron — empty means manual or dependency-triggered)
        </span>
        <span className="flex-1" />
        {!disabled && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="h-6 px-2 text-sm"
            onClick={() => setWizard(true)}
          >
            <Wand2 className="size-3.5" aria-hidden="true" />
            Build one
          </Button>
        )}
        {!disabled && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button type="button" variant="outline" size="sm" className="h-6 px-2 text-sm">
                Use a preset
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              {PRESETS.map((preset) => (
                <DropdownMenuItem key={preset.label} onSelect={() => onChange(preset.cron)}>
                  <span className="font-mono text-xs text-muted-subtle">
                    {preset.cron || '—'}
                  </span>
                  {preset.label}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>
      <Input
        id="cron"
        value={value}
        placeholder="0 2 * * *"
        aria-describedby="cron-help"
        onChange={(e) => {
          onChange(e.target.value);
          setField(fieldAt(e.target.value, e.target.selectionStart ?? 0));
        }}
        // Every way the caret can move: typing, arrows, clicking, tabbing in.
        // `selectionchange` on the document would catch them all in one, but it
        // fires for every selection on the page and this is one input.
        onSelect={(e) => setField(
          fieldAt(value, (e.target as HTMLInputElement).selectionStart ?? 0))}
        onKeyUp={(e) => setField(
          fieldAt(value, (e.target as HTMLInputElement).selectionStart ?? 0))}
        onFocus={(e) => setField(fieldAt(value, e.target.selectionStart ?? 0))}
        onBlur={() => setField(null)}
      />
      {/* The five names, with the one you are in picked out. An input cannot
          highlight inside itself without an overlay, and an overlay over a text
          box is a well-known way to end up one pixel out at some font size — so
          the help line does the pointing instead. */}
      <span id="cron-help" className="block font-mono text-code text-muted-subtle">
        {CRON_FIELDS.map((name, index) => (
          <span
            key={name}
            className={index === field ? 'font-semibold text-foreground' : undefined}
          >
            {name}{index < CRON_FIELDS.length - 1 ? ' ' : ''}
          </span>
        ))}
      </span>
      {preview && !preview.valid && (
        <span className="block text-base text-destructive">
          Not a schedule this server can run: {preview.error}
        </span>
      )}
      {preview?.valid && preview.next.length > 0 && (
        <div className="block text-base text-muted-foreground">
          Next:{' '}
          {/* One pill per run rather than a run-on line. Three instants separated
              by dots read as one string of characters; boxed, they read as three
              times, which is the question being answered. */}
          <span className="inline-flex flex-wrap items-center gap-1.5 align-middle">
            {preview.next.slice(0, 3).map((iso) => (
              <span
                key={iso}
                className="rounded-full border border-border bg-surface-panel-strong px-2 py-px font-mono text-code text-foreground"
              >
                {when(iso)}
              </span>
            ))}
          </span>{' '}
          — times are <strong>UTC</strong>
          {offset && `, and you are on ${offset}`}.
        </div>
      )}
      {wizard && (
        <CronWizard current={value} onUse={onChange} onClose={() => setWizard(false)} />
      )}
      {preview?.valid && preview.next.length === 0 && (
        <span className="block text-base text-status-warning">
          Valid, but it never comes round again — check the day and month.
        </span>
      )}
    </div>
  );
}
