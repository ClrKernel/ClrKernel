import { useCallback, useEffect, useRef } from 'react';

/**
 * A draggable divider. `vertical` splits left/right (the sidebar's edge);
 * horizontal splits top/bottom (editor above output).
 *
 * Drags are reported on animation frames, not on every mousemove: the consumer
 * relayouts Monaco on each one, and Monaco's layout is expensive enough that an
 * unthrottled drag stutters. Pointer capture keeps the drag alive when the
 * cursor leaves the handle — including over the Monaco panes on either side,
 * which would otherwise swallow the events.
 */
export function Splitter({
  orientation,
  onDrag,
  onReset,
  label,
}: {
  orientation: 'vertical' | 'horizontal';
  /** The pointer's client X (vertical) or Y (horizontal) during the drag. */
  onDrag: (position: number) => void;
  /** Double-click: back to the default split. */
  onReset: () => void;
  label: string;
}) {
  const frame = useRef<number | null>(null);
  const pending = useRef(0);
  const latestOnDrag = useRef(onDrag);
  latestOnDrag.current = onDrag;

  useEffect(() => () => {
    if (frame.current != null) {
      cancelAnimationFrame(frame.current);
    }
  }, []);

  const schedule = useCallback((position: number) => {
    pending.current = position;
    if (frame.current != null) {
      return;
    }
    frame.current = requestAnimationFrame(() => {
      frame.current = null;
      latestOnDrag.current(pending.current);
    });
  }, []);

  return (
    <div
      className={`focus-splitter focus-splitter-${orientation}`}
      role="separator"
      aria-orientation={orientation}
      aria-label={label}
      title={`${label} — drag to resize, double-click to reset`}
      onDoubleClick={onReset}
      onPointerDown={(event) => {
        // Capture on the handle itself: without it the drag dies the moment the
        // pointer crosses into an editor.
        event.currentTarget.setPointerCapture(event.pointerId);
        event.preventDefault();
      }}
      onPointerMove={(event) => {
        if (event.currentTarget.hasPointerCapture(event.pointerId)) {
          schedule(orientation === 'vertical' ? event.clientX : event.clientY);
        }
      }}
      onPointerUp={(event) => {
        event.currentTarget.releasePointerCapture(event.pointerId);
        // One final, unthrottled update so the released position is exact.
        latestOnDrag.current(orientation === 'vertical' ? event.clientX : event.clientY);
      }}
    />
  );
}
