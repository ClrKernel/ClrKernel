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
export function notebookPath(entered: string, extension = '.nb.md'): string | null {
  const cleaned = (entered ?? '').trim().replace(/\\/g, '/').replace(/\/+/g, '/');
  const segments = cleaned.split('/').filter((s) => s !== '' && s !== '.');
  // `..` is refused here as well as at the server, so the message names the
  // typing mistake rather than arriving as "path is outside your branch".
  if (segments.length === 0 || segments.some((s) => s === '..')) {
    return null;
  }
  const path = segments.join('/');
  const name = segments[segments.length - 1].toLowerCase();
  // `extension` is what a bare name gets, and it is also an extension to keep:
  // renaming a `*.jobs.yaml` must not produce `nightly.jobs.yaml.nb.md`, and
  // typing a notebook name while renaming one still means a notebook.
  if ([...EXTENSIONS, extension].some((e) => name.endsWith(e))) {
    return path;
  }
  // `notes.md` means `notes.nb.md`. Appending would give `notes.md.nb.md`, and
  // a plain `.md` is not something the server will open as a notebook.
  return extension === '.nb.md' && name.endsWith('.md')
    ? `${path.slice(0, -3)}.nb.md`
    : `${path}${extension}`;
}

const PROMPT =
  'New notebook — a path under the notebooks root.\n\n'
  + 'Folders are made as needed, so reports/monthly creates the folder too.';

/**
 * Asks for a path, and keeps asking while what comes back is not one — with what
 * was typed still in the box, so a typo is corrected rather than retyped. Null
 * when the person cancelled.
 */
export function promptForNotebook(
  folder = '', name = '', prompt = PROMPT, extension = '.nb.md',
): string | null {
  let seed = `${folder ? `${folder}/` : ''}${name}`;
  for (;;) {
    const entered = window.prompt(prompt, seed);
    if (entered == null) {
      return null;
    }
    const path = notebookPath(entered, extension);
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
export async function createNotebook(path: string, content = NEW_NOTEBOOK): Promise<void> {
  const existing = await api.notebookContent('mine', path).catch(() => null);
  if (existing != null) {
    throw new Error(`${path} is already on your branch.`);
  }
  await api.saveNotebookContent(path, content);
}

/** A jobs file stays a jobs file; everything else is a notebook. */
function extensionOf(path: string): string {
  return path.toLowerCase().endsWith('.jobs.yaml') ? '.jobs.yaml' : '.nb.md';
}

const SAVE_AS_PROMPT =
  'Save a copy as — a path under the notebooks root on your branch.\n\n'
  + 'Folders are made as needed, so reports/monthly creates the folder too.';

const MOVE_PROMPT =
  'Move to — a path under the notebooks root on your branch.\n\n'
  + 'Renaming is the same thing: a notebook\u2019s path is its name.';

/**
 * Asks where, and writes a copy there. Returns the path, or null if cancelled.
 *
 * Always onto your own branch, whichever branch the copy came from — the same
 * rule every other write in this app follows, and the reason this is usable from
 * a read-only branch at all.
 */
export async function saveNotebookAs(content: string, seed: string): Promise<string | null> {
  const wanted = promptForNotebook('', seed, SAVE_AS_PROMPT, extensionOf(seed));
  if (wanted == null) {
    return null;
  }
  await createNotebook(wanted, content);
  return wanted;
}

/**
 * Asks where, warns about the jobs that name the old path, and moves it.
 *
 * The warning is the whole reason this is not two lines at the call site. A
 * `*.jobs.yaml` names its notebook by path, so moving one silently breaks
 * every job that runs it — and the failure surfaces at the next scheduled run,
 * in a log nobody is watching. Naming them costs one request.
 */
export async function moveNotebookTo(from: string, seed: string): Promise<string | null> {
  const wanted = promptForNotebook('', seed, MOVE_PROMPT, extensionOf(seed));
  if (wanted == null || wanted === from) {
    return null;
  }
  // Every environment's jobs, not just this branch's: a job in test names the
  // path you are about to move on your own branch, and it breaks when you push.
  const jobs = await api.jobs()
    .then((reply) => reply.jobs.filter((job) => job.notebook === from).map((job) => job.name))
    .catch(() => [] as string[]);
  if (jobs.length > 0 && !confirm(
    `${jobs.length === 1 ? 'A job runs' : `${jobs.length} jobs run`} this notebook by its path `
    + `(${jobs.join(', ')}). Moving it breaks ${jobs.length === 1 ? 'that job' : 'them'} until `
    + 'the jobs file is pointed at the new path.\n\nMove it anyway?')) {
    return null;
  }
  await api.moveNotebook(from, wanted);
  return wanted;
}


/**
 * The jobs file paired with a notebook. Derived, not stored: `etl.nb.md` is
 * scheduled by `etl.jobs.yaml` and by nothing else, which is what makes the pair
 * one promotable unit.
 */
export function jobsFileFor(notebook: string): string {
  return notebook.replace(/\.nb\.md$/i, '') + '.jobs.yaml';
}

/** The job name a fresh jobs file starts with: the notebook's own. */
export function firstJobName(notebook: string): string {
  return (notebook.split('/').pop() ?? 'daily').replace(/\.nb\.md$/i, '');
}

/**
 * Create the jobs file beside a notebook, or do nothing if it is already there.
 *
 * Returns the path either way, because both answers mean the same thing to the
 * caller: go and open it. Shared, because "add a job" is now asked from the
 * Files list *and* from the editor — where the person who needs it is standing
 * when promotion tells them a notebook with no job cannot prove itself.
 */
export async function ensureJobsFile(notebook: string): Promise<string> {
  const path = jobsFileFor(notebook);
  try {
    await createNotebook(path, `jobs:\n  - name: ${firstJobName(notebook)}\n`);
  } catch (e) {
    // Already there is not an error — it is where you were going anyway.
    if (!/already on your branch/i.test((e as Error).message)) {
      throw e;
    }
  }
  return path;
}
