import { describe, expect, it } from 'vitest';
import { STATUS_LABEL, statusOf } from './autosave';

describe('statusOf', () => {
  it('says what is true of the buffer', () => {
    expect(statusOf(false, false, false)).toBe('saved');
    expect(statusOf(true, false, false)).toBe('unsaved');
    expect(statusOf(true, true, false)).toBe('saving');
  });

  it('keeps saying "saving" when an edit lands mid-write', () => {
    // The buffer moved on while the previous contents were going out. Something
    // is genuinely being written, and the newer edit is covered by the pass that
    // follows — reading "unsaved" here would flicker on every keystroke.
    expect(statusOf(true, true, false)).toBe('saving');
    expect(statusOf(false, true, false)).toBe('saving');
  });

  it('lets a failure outrank everything', () => {
    // Including the case where the buffer happens to match what was last written:
    // a write that did not land is the thing worth saying, not a stale "Saved".
    expect(statusOf(false, false, true)).toBe('failed');
    expect(statusOf(true, true, true)).toBe('failed');
  });

  it('has a label for every state', () => {
    for (const status of ['saved', 'unsaved', 'saving', 'failed'] as const) {
      expect(STATUS_LABEL[status]).toBeTruthy();
    }
  });
});
