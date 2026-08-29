import { useCallback, useState } from 'react';
import { dropIndex, dropSide, type DropSide } from '../reorder';

/**
 * Dragging a cell to a new place, shared by both contents views.
 *
 * HTML5 drag-and-drop rather than pointer events: it is what the browser gives
 * you for free — a drag image, an escape key that cancels, a cursor that says
 * what will happen — and reimplementing those on pointermove is how a reorder
 * ends up feeling like a homemade one. The panel scrolls during a drag for free
 * too, which a hand-rolled version would have to do itself.
 *
 * The arithmetic lives in `reorder.ts` and is tested there; what is here is the
 * event plumbing and the one piece of state a drop indicator needs.
 */
export interface CellDrag {
  /** The index being dragged, or null. */
  dragging: number | null;
  /** Where the indicator sits right now, or null when the pointer is nowhere. */
  over: { index: number; side: DropSide } | null;
  /** Spread onto each row. `index` is the cell's position in the notebook. */
  rowProps: (index: number) => {
    draggable: boolean;
    onDragStart: (event: React.DragEvent) => void;
    onDragEnd: () => void;
    onDragOver: (event: React.DragEvent) => void;
    onDrop: (event: React.DragEvent) => void;
    'data-drop'?: DropSide;
  };
}

export function useCellDrag(
  onMove: (from: number, to: number) => void,
  enabled = true,
): CellDrag {
  const [dragging, setDragging] = useState<number | null>(null);
  const [over, setOver] = useState<{ index: number; side: DropSide } | null>(null);

  const rowProps = useCallback((index: number) => ({
    draggable: enabled,
    onDragStart: (event: React.DragEvent) => {
      setDragging(index);
      // Firefox starts no drag at all without data on the transfer, and `move`
      // is what gives the cursor its "this will be moved" shape.
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', String(index));
    },
    onDragEnd: () => {
      setDragging(null);
      setOver(null);
    },
    onDragOver: (event: React.DragEvent) => {
      // Unconditionally, and this is load-bearing: without preventDefault the
      // browser refuses the drop entirely. Gating it on `dragging` looked
      // tidier and was wrong — these props were built on the render *before*
      // dragstart set that state, so the first dragover sees null, declines to
      // allow the drop, and a short drag never gets a second one. It failed
      // exactly once out of every once.
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
      const side = dropSide(event.clientY, box.top, box.height);
      setOver((current) =>
        current?.index === index && current.side === side ? current : { index, side });
    },
    onDrop: (event: React.DragEvent) => {
      event.preventDefault();
      // The transfer, not the state, for the same reason: it was written at
      // dragstart by the browser and cannot be a render behind. State is the
      // fallback for a browser that hands back nothing.
      const carried = Number(event.dataTransfer.getData('text/plain'));
      const from = Number.isInteger(carried) && carried >= 0 ? carried : dragging;
      setDragging(null);
      setOver(null);
      if (from == null) {
        return;
      }
      const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
      const to = dropIndex(from, index, dropSide(event.clientY, box.top, box.height));
      // A drop that changes nothing must not become an undo entry — pressing
      // Ctrl+Z afterwards would then appear to do nothing at all.
      if (to !== from) {
        onMove(from, to);
      }
    },
    ...(over?.index === index ? { 'data-drop': over.side } : {}),
  }), [dragging, over, onMove, enabled]);

  return { dragging, over, rowProps };
}
