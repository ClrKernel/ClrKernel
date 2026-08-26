import { describe, expect, it } from 'vitest';
import { textToRun, type RunnableEditor } from './runTarget';

/** An editor whose selection is expressed as the text inside it. */
function editorWith(value: string, selected: string | null): RunnableEditor {
  return {
    getSelection: () => (selected == null ? null : ({} as never)),
    getModel: () => ({ getValueInRange: () => selected ?? '' }),
    getValue: () => value,
  };
}

describe('textToRun', () => {
  it('runs the selection when there is one', () => {
    expect(textToRun(editorWith('SELECT 1;\nSELECT 2;', 'SELECT 2;'))).toBe('SELECT 2;');
  });

  it('and the whole buffer when there is not', () => {
    // The bug this exists for: the Run button ran this branch even with a
    // selection, so highlighting one statement and clicking ran all of them.
    expect(textToRun(editorWith('SELECT 1;\nSELECT 2;', null))).toBe('SELECT 1;\nSELECT 2;');
    expect(textToRun(editorWith('SELECT 1;\nSELECT 2;', ''))).toBe('SELECT 1;\nSELECT 2;');
  });

  it('treats a whitespace-only selection as no selection', () => {
    // A click leaves an empty range; a drag past the last character can leave a
    // newline. Running either would be a Run button that does nothing.
    expect(textToRun(editorWith('SELECT 1;', '   \n '))).toBe('SELECT 1;');
  });

  it('keeps the selection verbatim, including its own whitespace', () => {
    // Not trimmed: line numbers in a SQL error should match what you selected.
    expect(textToRun(editorWith('x', '\nSELECT 2;\n'))).toBe('\nSELECT 2;\n');
  });

  it('is empty when there is no editor yet', () => {
    expect(textToRun(null)).toBe('');
    expect(textToRun(undefined)).toBe('');
  });
});
