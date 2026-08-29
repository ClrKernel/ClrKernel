import { useId, type ReactNode } from 'react';
import { Checkbox } from '@/components/ui/checkbox';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select';
import { cn } from '@/lib/utils';

/**
 * One labelled control, everywhere.
 *
 * The app grew three of these — a bare `<label>` around an Input, a
 * `.form-field` with a raw `<input>` in it, and `.form label` which only styled
 * anything inside a `.form` — so the same field looked different depending on
 * which page you were on, and the raw controls were whatever the browser draws.
 * This is the one shape; nothing else should be declaring label spacing.
 */
export function Field({
  label, hint, required, htmlFor, className, children,
}: {
  label: ReactNode;
  /** Under the control: what it means, or what is wrong with it. */
  hint?: ReactNode;
  required?: boolean;
  /** Set when the control is not a direct child — a `<label>` cannot wrap one
   *  that contains a button, because the button would take the label's text as
   *  its accessible name. */
  htmlFor?: string;
  className?: string;
  children: ReactNode;
}) {
  const content = (
    <>
      <span className="text-sm font-medium">
        {label}
        {required && <span className="text-status-warning"> *</span>}
      </span>
      {children}
      {hint && <span className="text-base text-muted-foreground">{hint}</span>}
    </>
  );
  return htmlFor ? (
    <div className={cn('flex flex-col gap-1', className)}>{content}</div>
  ) : (
    <label className={cn('flex flex-col gap-1', className)} htmlFor={htmlFor}>{content}</label>
  );
}

/**
 * A heading inside a form, and what the section is for.
 *
 * A bare `<h3>` gets the document's heading size, which in a dialog is either
 * shouting or — with the reset this app uses — indistinguishable from a field
 * label. This is the size a section divider wants to be.
 */
export function FieldSection({
  title, description, children,
}: {
  title: ReactNode;
  description?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="flex flex-col gap-2">
      <div className="flex flex-col gap-1">
        <h3 className="text-sm font-semibold tracking-tight">{title}</h3>
        {description && (
          <p className="text-base text-muted-foreground">{description}</p>
        )}
      </div>
      {children}
    </section>
  );
}

/**
 * A form's fields, two or three across when there is room and stacked when there
 * is not. `auto-fit` rather than a breakpoint: these live in dialogs as well as on
 * pages, and the width that matters is the container's, not the window's.
 */
export function FieldGrid({ className, children }: { className?: string; children: ReactNode }) {
  return (
    <div
      className={cn('grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(16rem,1fr))]', className)}
    >
      {children}
    </div>
  );
}

/** Fields side by side — the short ones that read as a group. */
export function FieldRow({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('flex flex-wrap items-end gap-3', className)}>{children}</div>;
}

/**
 * A checkbox and its label, on one line.
 *
 * `onChange` takes a boolean rather than an event: Radix reports an indeterminate
 * state as `"indeterminate"`, and every caller here wants a yes or no.
 */
export function CheckboxField({
  label, hint, checked, onChange, disabled, className,
}: {
  label: ReactNode;
  hint?: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  className?: string;
}) {
  const id = useId();
  return (
    <div className={cn('flex flex-col gap-1', className)}>
      <div className="flex items-center gap-2">
        <Checkbox
          id={id}
          checked={checked}
          disabled={disabled}
          onCheckedChange={(value) => onChange(value === true)}
        />
        <label htmlFor={id} className="text-sm font-medium leading-none">{label}</label>
      </div>
      {hint && <span className="text-base text-muted-foreground">{hint}</span>}
    </div>
  );
}

/**
 * A dropdown that looks like the rest of the app rather than like the operating
 * system. The empty string is not a usable Radix item value, so an option meaning
 * "nothing chosen" is carried as `placeholder` instead.
 */
export function SelectField({
  label, hint, required, value, onChange, options, placeholder, clearLabel, disabled, className,
}: {
  label: ReactNode;
  hint?: ReactNode;
  required?: boolean;
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: ReactNode }[];
  /** Shown when `value` is empty. */
  placeholder?: string;
  /** Offers an option meaning "no value", labelled with this. */
  clearLabel?: string;
  disabled?: boolean;
  className?: string;
}) {
  const id = useId();
  return (
    <Field label={label} hint={hint} required={required} htmlFor={id} className={className}>
      <Select
        value={value || undefined}
        onValueChange={(chosen) => onChange(chosen === _clear ? '' : chosen)}
        disabled={disabled}
      >
        <SelectTrigger id={id} className="w-full">
          <SelectValue placeholder={placeholder ?? 'Choose…'} />
        </SelectTrigger>
        <SelectContent>
          {clearLabel && <SelectItem value={_clear}>{clearLabel}</SelectItem>}
          {options.map((option) => (
            <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  );
}

/** Radix refuses an empty item value, so "no value" travels as this and is mapped
 *  back to the empty string on the way out. */
const _clear = '\u0000none';
