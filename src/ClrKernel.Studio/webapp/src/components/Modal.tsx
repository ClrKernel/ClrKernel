import { type ReactNode } from 'react';
import { Button } from '@/components/ui/button';

/**
 * The one dialog shell.
 *
 * It does **not** close on a click outside. Every one of these holds a form
 * somebody has been typing into — a connection, a password, a schedule — and
 * throwing that away because the pointer landed two pixels off is not a
 * shortcut anyone asked for. The ways out are Cancel and ✕, and both are always
 * on screen.
 */
export function Modal({ title, onClose, wide = false, footer, children }: {
  title: ReactNode;
  onClose: () => void;
  /** For the ones that show a table rather than a form. */
  wide?: boolean;
  /** The actions. Kept out of the scrolling area so a long form never hides its
   *  own Save button — which a dialog capped at 80vh otherwise does as soon as
   *  somebody adds a field. */
  footer?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="modal-backdrop">
      <div className={wide ? 'modal modal-wide' : 'modal'}>
        <div className="flex items-start justify-between gap-4">
          <h2 style={{ margin: 0 }}>{title}</h2>
          <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={onClose}>✕</Button>
        </div>
        {/* The gap the dialog's sections sit in. Without it they stack on whatever
            margins they happen to have, which is how a heading ends up welded to
            the field above it. */}
        <div className="mt-4 flex min-h-0 flex-1 flex-col gap-4 overflow-auto">{children}</div>
        {footer && (
          <div className="mt-4 flex items-center gap-2 border-t border-border pt-4">{footer}</div>
        )}
      </div>
    </div>
  );
}
