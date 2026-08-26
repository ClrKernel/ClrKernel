import type * as monacoType from 'monaco-editor';

/**
 * What "run this" means, for every control that offers it.
 *
 * Type-only Monaco import, like `lsp.ts`, so this can be tested without the
 * editor bundle — and it is worth testing, because it was wrong. Ctrl+Enter and
 * F5 are bound inside the editor and ran the selection; the Run button is on the
 * toolbar outside it and ran the whole buffer. Highlighting one statement and
 * then reaching for the mouse ran all of them. Two controls that promise the
 * same thing need one function saying what that thing is.
 *
 * Whitespace is not a selection. Clicking into the editor leaves an empty range,
 * and a drag that ends past the last character can leave a bare newline — either
 * one taken literally is a Run button that appears to do nothing.
 */
export function textToRun(editor: RunnableEditor | null | undefined): string {
  if (editor == null) {
    return '';
  }
  const selection = editor.getSelection();
  const selected = selection == null ? '' : (editor.getModel()?.getValueInRange(selection) ?? '');
  return selected.trim().length > 0 ? selected : editor.getValue();
}

/** Just the three calls this needs, so a test can stand in for an editor. */
export interface RunnableEditor {
  getSelection(): monacoType.Selection | null;
  getModel(): { getValueInRange(range: monacoType.IRange): string } | null;
  getValue(): string;
}
