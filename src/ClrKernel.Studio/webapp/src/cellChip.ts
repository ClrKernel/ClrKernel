import type { ApiLanguage } from './api';
import type { EditorCell } from './notebook';

/**
 * The little monogram beside a cell in the contents list — `TSQL`, `ORA`, `C#`.
 *
 * What it is for: a notebook mixing C#, SQL, HTTP and prose is only scannable if
 * you can tell the cells apart without reading them. The letters do that work;
 * the colour only helps the eye group them.
 *
 * The letters come from the kernel (`ICellLanguage.Monogram`), so a language
 * plugged in at run time gets a correct chip with no change here. The two that
 * cannot come from there are the two that are not registered cell languages at
 * all: C# is the engine's own, and Markdown is prose. They are named below for
 * the same reason `languageOptions` prepends them to the picker.
 */

export const CSHARP_CHIP = 'C#';
export const MARKDOWN_CHIP = 'MD';

/** How many hues the token layer defines: `--lang-1` … `--lang-6`. */
export const CHIP_HUES = 6;

export interface CellChip {
  /** Two to four characters. */
  label: string;
  /** The full name, for a tooltip and for the accessible name. */
  title: string;
  /** A `--lang-N` custom property, never a colour. */
  colorVar: string;
}

/**
 * A stable hue for a language id.
 *
 * Deterministic rather than configured: which of six hues a language lands on
 * does not need to mean anything, it only needs to be the same every time you
 * open the notebook. Two languages can collide and that is fine — the letters
 * are what identify a cell, and a chip is not a legend.
 */
export function hueFor(key: string): number {
  let hash = 0;
  for (let i = 0; i < key.length; i += 1) {
    // The classic 31-multiplier string hash, kept in 32 bits so it cannot drift
    // into float territory and give a different answer on a longer id.
    hash = (Math.imul(hash, 31) + key.charCodeAt(i)) | 0;
  }
  return (Math.abs(hash) % CHIP_HUES) + 1;
}

/** The chip for a cell, or null for one that does not want one. */
export function chipFor(cell: EditorCell, languages: ApiLanguage[]): CellChip {
  if (cell.kind === 'markdown') {
    return { label: MARKDOWN_CHIP, title: 'Markdown', colorVar: `--lang-${hueFor('markdown')}` };
  }
  const language = languages.find((l) => l.id === cell.languageId);
  if (language == null) {
    // No languageId on a code cell means C#, which is the engine's own language
    // and has no descriptor to ask.
    return { label: CSHARP_CHIP, title: 'C#', colorVar: `--lang-${hueFor('csharp')}` };
  }
  return {
    // A descriptor from a kernel that predates the field still has to produce
    // something, and its id cut to four is what that kernel would have said.
    label: language.monogram ?? language.id.toUpperCase().slice(0, 4),
    title: language.displayName,
    colorVar: `--lang-${hueFor(language.id)}`,
  };
}
