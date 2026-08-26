import { previewKey } from '../thumbnail';
import { monaco } from './setup';

/**
 * Syntax-coloured HTML for a snippet, without building an editor for it.
 *
 * `monaco.editor.colorize` runs the same tokenizer an editor uses and returns
 * markup — no DOM, no model, no editor instance. That is the whole reason the
 * thumbnail column is affordable: twenty editors in a sidebar would make
 * scrolling miserable and burn memory for previews nobody can read anyway.
 *
 * The returned HTML carries `.mtkN` classes rather than inline colours, and
 * Monaco writes the stylesheet behind those classes when the theme is set — so
 * a cached string recolors itself on a theme change and the cache is keyed on
 * the text alone. See `previewKey`.
 */

const cache = new Map<string, string>();

/** In flight, so a column of twenty thumbnails scrolling past does not become
 *  twenty duplicate tokenizer runs of the same cell. */
const running = new Map<string, Promise<string>>();

/**
 * Bounded, because a long session editing a long notebook would otherwise keep
 * every version of every cell it ever rendered. Oldest out first: a Map iterates
 * in insertion order, which is the only reason this is two lines.
 */
const MAX_ENTRIES = 400;

export function colorized(cellId: string, language: string, source: string): string | undefined {
  return cache.get(previewKey(cellId, language, source));
}

/**
 * Colorizes and caches. Resolves with the HTML; call `colorized` afterwards for
 * the synchronous read a render needs.
 */
export async function colorize(cellId: string, language: string, source: string): Promise<string> {
  const key = previewKey(cellId, language, source);
  const cached = cache.get(key);
  if (cached != null) {
    return cached;
  }
  const already = running.get(key);
  if (already != null) {
    return already;
  }
  const work = monaco.editor
    .colorize(source, language, { tabSize: 4 })
    .then((html) => {
      if (cache.size >= MAX_ENTRIES) {
        const oldest = cache.keys().next();
        if (!oldest.done) {
          cache.delete(oldest.value);
        }
      }
      cache.set(key, html);
      return html;
    })
    .catch(() => {
      // A language Monaco has no tokenizer for, or a snippet it chokes on. The
      // thumbnail falls back to plain text, which is still a recognisable shape
      // — a preview is not worth an error boundary.
      const plain = escapeHtml(source);
      cache.set(key, plain);
      return plain;
    })
    .finally(() => running.delete(key));
  running.set(key, work);
  return work;
}

/** Drops everything — for a notebook being closed, or a test. */
export function forgetColorized(): void {
  cache.clear();
  running.clear();
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
