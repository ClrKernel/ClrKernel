/**
 * The autosave state machine, without React.
 *
 * Kept apart from the hook so the transitions can be tested directly: the
 * interesting cases are all about ordering — an edit landing while a write is in
 * flight, a failure that must not be forgotten, a flush racing the debounce —
 * and none of them need a DOM.
 */

export type SaveStatus = 'saved' | 'unsaved' | 'saving' | 'failed';

/** How long after you stop typing the buffer is written. */
export const AUTOSAVE_DELAY = 800;

/**
 * What the toolbar shows.
 *
 * `dirty` is the editor's own comparison of buffer against last-written. A write
 * in flight still reads as "saving" even once the buffer has moved on again,
 * because something *is* being written; the edit that arrived during it is
 * covered by the pass that follows.
 */
export function statusOf(
  dirty: boolean,
  writing: boolean,
  failed: boolean,
): SaveStatus {
  if (failed) {
    return 'failed';
  }
  if (writing) {
    return 'saving';
  }
  return dirty ? 'unsaved' : 'saved';
}

export const STATUS_LABEL: Record<SaveStatus, string> = {
  saved: 'Saved',
  unsaved: 'Unsaved',
  saving: 'Saving…',
  failed: 'Save failed',
};

export const STATUS_TITLE: Record<SaveStatus, string> = {
  saved: 'Everything is written to your branch. Push to test when you are ready.',
  unsaved: 'Writing to your branch in a moment. ⌘S / Ctrl+S writes now.',
  saving: 'Writing to your branch.',
  failed: 'The last write did not land. Click to try again.',
};
