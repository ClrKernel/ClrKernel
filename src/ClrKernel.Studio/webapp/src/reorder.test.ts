import { describe, expect, it } from 'vitest';
import { dropIndex, dropSide, stepIndex } from './reorder';

describe('dropSide', () => {
  it('flips at the row’s midpoint', () => {
    expect(dropSide(105, 100, 20)).toBe('before');
    expect(dropSide(115, 100, 20)).toBe('after');
    expect(dropSide(110, 100, 20)).toBe('after');
  });

  it('uses the row’s own height, not a fixed threshold', () => {
    // An outline row is 20px and a thumbnail is 200. A constant would put the
    // flip somewhere arbitrary in one of the two.
    expect(dropSide(150, 100, 200)).toBe('before');
    expect(dropSide(150, 100, 20)).toBe('after');
  });
});

describe('dropIndex', () => {
  it('accounts for the cell being removed before it is re-inserted', () => {
    // The off-by-one every hand-rolled reorder has: dropping cell 2 after cell 5
    // is index 5 in the final list, not 6, because 3, 4 and 5 all shifted down.
    expect(dropIndex(2, 5, 'after')).toBe(5);
    expect(dropIndex(2, 5, 'before')).toBe(4);
  });

  it('and does not when moving up, where nothing below has shifted', () => {
    expect(dropIndex(5, 2, 'before')).toBe(2);
    expect(dropIndex(5, 2, 'after')).toBe(3);
  });

  it('reports a no-op as a no-op, so nothing lands in the undo history', () => {
    // Either side of yourself, and just after the cell you were already below.
    expect(dropIndex(3, 3, 'before')).toBe(3);
    expect(dropIndex(3, 3, 'after')).toBe(3);
    expect(dropIndex(3, 2, 'after')).toBe(3);
  });

  it('moves a cell to the very top and the very bottom', () => {
    expect(dropIndex(4, 0, 'before')).toBe(0);
    expect(dropIndex(0, 4, 'after')).toBe(4);
  });

  it('round-trips: moving a cell and moving it back is where it started', () => {
    const cells = ['a', 'b', 'c', 'd', 'e'];
    const move = (list: string[], from: number, to: number) => {
      const next = [...list];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    };
    const moved = move(cells, 1, dropIndex(1, 4, 'after'));
    expect(moved).toEqual(['a', 'c', 'd', 'e', 'b']);
    expect(move(moved, 4, dropIndex(4, 0, 'after'))).toEqual(cells);
  });
});

describe('stepIndex', () => {
  it('moves one place', () => {
    expect(stepIndex(2, 1, 5)).toBe(3);
    expect(stepIndex(2, -1, 5)).toBe(1);
  });

  it('and stops at the ends rather than wrapping', () => {
    // Wrapping would send the first cell to the bottom of the notebook on a
    // keystroke meant to nudge it, which is not a thing anybody wants undone.
    expect(stepIndex(0, -1, 5)).toBe(0);
    expect(stepIndex(4, 1, 5)).toBe(4);
  });
});
