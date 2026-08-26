/**
 * Dragging a cell to a new place in the contents panel.
 *
 * React-free, like the rest of the logic in this app: the awkward part of
 * drag-and-drop is not the events, it is the arithmetic — where a drop actually
 * lands, and what "after the cell I picked up" means once that cell has been
 * removed from the list. Both are worth being able to check by calling a
 * function.
 */

/** Which half of a row the pointer is over, which decides before or after. */
export type DropSide = 'before' | 'after';

/**
 * Which side of `element` a pointer at `clientY` is on.
 *
 * The midpoint, so the target flips exactly where the eye expects it to. A row's
 * own height rather than a fixed threshold: an outline row is 20px and a
 * thumbnail is 200, and a constant would put the flip somewhere arbitrary in one
 * of them.
 */
export function dropSide(clientY: number, top: number, height: number): DropSide {
  return clientY < top + height / 2 ? 'before' : 'after';
}

/**
 * Where a cell dragged from `from` ends up when dropped on `onto`.
 *
 * The subtlety, and the reason this is not `to = onto + 1`: the dragged cell is
 * removed before it is re-inserted, so every index above it shifts down by one.
 * Dropping cell 2 after cell 5 means index 5 in the final list, not 6 — and
 * getting that wrong puts the cell one place further than you dropped it, which
 * is the classic off-by-one of every hand-rolled reorder.
 *
 * Returns `from` when the move is a no-op, so callers can skip the edit and the
 * undo entry that would come with it.
 */
export function dropIndex(from: number, onto: number, side: DropSide): number {
  const target = side === 'after' ? onto + 1 : onto;
  // Dropping either side of yourself, or immediately after the cell you were
  // already below, changes nothing.
  if (target === from || target === from + 1) {
    return from;
  }
  return target > from ? target - 1 : target;
}

/**
 * Where Alt+↑ / Alt+↓ moves a cell — one place, clamped at the ends.
 *
 * The keyboard half of the same feature. Drag-and-drop is a pointer gesture and
 * nothing else, so a notebook could not be reordered from the keyboard at all;
 * VS Code binds these to moving a line, and moving a cell is the same idea one
 * level up.
 */
export function stepIndex(from: number, delta: number, count: number): number {
  const to = from + delta;
  return to < 0 || to >= count ? from : to;
}
