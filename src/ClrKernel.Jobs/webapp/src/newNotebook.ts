import { api } from './api';

/**
 * Making a notebook: turning what somebody typed into a path the server will
 * accept, and putting something in it.
 *
 * React-free so it can be unit-tested, which is the rule for this folder — and
 * this one earns it: every rejection here is a 400 the user would otherwise meet
 * only after the file appeared to be created.
 */

/** What the server will open as a notebook. Anything else gets `.nb.md` added. */
const EXTENSIONS = ['.nb.md', '.ipynb', '.dib', '.csx'];

/**
 * One empty C# cell, not an empty file.
 *
 * A `.nb.md` with nothing in it parses as prose, and a notebook that opens with
 * no editor in it reads as broken rather than as new.
 */
export const NEW_NOTEBOOK = '```csharp\n\n```\n';

/**
 * The path to create, or null when what was typed is not one.
 *
 * Folders come from the path — `reports/monthly` makes `reports/`. There is no
 * separate "new folder" because there is nothing it could produce: git does not
 * track an empty directory, and the file tree prunes one.
 */
export function notebookPath(entered: string): string | null {
  const cleaned = (entered ?? '').trim().replace(/\\/g, '/').replace(/\/+/g, '/');
  const segments = cleaned.split('/').filter((s) => s !== '' && s !== '.');
  // `..` is refused here as well as at the server, so the message names the
  // typing mistake rather than arriving as "path is outside your branch".
  if (segments.length === 0 || segments.some((s) => s === '..')) {
    return null;
  }
  const path = segments.join('/');
  const name = segments[segments.length - 1].toLowerCase();
  if (EXTENSIONS.some((e) => name.endsWith(e))) {
    return path;
  }
  // `notes.md` means `notes.nb.md`. Appending would give `notes.md.nb.md`, and
  // a plain `.md` is not something the server will open as a notebook.
  return name.endsWith('.md') ? `${path.slice(0, -3)}.nb.md` : `${path}.nb.md`;
}

const PROMPT =
  'New notebook — a path under the notebooks root.\n\n'
  + 'Folders are made as needed, so reports/monthly creates the folder too.';

/**
 * Asks for a path, and keeps asking while what comes back is not one — with what
 * was typed still in the box, so a typo is corrected rather than retyped. Null
 * when the person cancelled.
 */
export function promptForNotebook(folder = ''): string | null {
  let seed = folder ? `${folder}/` : '';
  for (;;) {
    const entered = window.prompt(PROMPT, seed);
    if (entered == null) {
      return null;
    }
    const path = notebookPath(entered);
    if (path != null) {
      return path;
    }
    seed = entered;
  }
}

/**
 * Creates it on your own branch — always yours, whichever branch you were
 * reading — and refuses to land on a file that is already there.
 *
 * The check is a read rather than a flag on the write: a save is a blind
 * overwrite, and "new notebook" quietly emptying an existing one is the worst
 * available reading of the button.
 */
export async function createNotebook(path: string): Promise<void> {
  const existing = await api.notebookContent('mine', path).catch(() => null);
  if (existing != null) {
    throw new Error(`${path} is already on your branch.`);
  }
  await api.saveNotebookContent(path, NEW_NOTEBOOK);
}
