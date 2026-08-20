/**
 * The next free `Untitled-N.nb.md` given the notebook paths already open.
 *
 * (The #!sql-connect / #!dax-connect builders that used to live here are gone:
 * connect lines are composed generically from the kernel's connection-provider
 * descriptors — see connectionDirective.ts.)
 *
 * The double extension is the point: the notebook type is selected by the `*.nb.md` pattern, so a
 * new file called `Untitled.md` is not one of our notebooks and saving it needs the name corrected
 * by hand. Numbering matches the editor's own convention for untitled files.
 */
export function nextUntitledNotebookName(openPaths: readonly string[], suffix = '.nb.md'): string {
    const taken = new Set(openPaths.map((p) => p.replace(/^.*[\\/]/, '')));
    for (let n = 1; ; n++) {
        const candidate = `Untitled-${n}${suffix}`;
        if (!taken.has(candidate)) {
            return candidate;
        }
    }
}
