import type { ApiCommit } from './api';

/** One entry of a commit's name-status list: a path, and what happened to it. */
export type Change = ApiCommit['files'][number];

/** A row of the changed-files tree: a folder, or a file with what happened to it. */
interface Row {
  key: string;
  name: string;
  depth: number;
  change: Change | null;
}

/**
 * The commit's files as a tree, flattened for rendering.
 *
 * <p>Folders are not collapsible here, unlike the main explorer. This list is one
 * commit's worth of files and it exists to be read whole; hiding part of "what
 * changed" behind a chevron is the opposite of what the page is for.</p>
 */
export function changedTree(files: Change[]): Row[] {
  const rows: Row[] = [];
  let previous: string[] = [];
  // Sorted so a folder's files land together and its heading is written once.
  for (const change of [...files].sort((a, b) => a.path.localeCompare(b.path))) {
    const parts = change.path.split('/');
    const folders = parts.slice(0, -1);
    // Folders are written only where they differ from the row above — the same
    // heading over every file in a folder is noise the indentation already says.
    // Once one differs, every folder below it is new too, even where the name
    // repeats: `c/b` is not `a/b`, and comparing depth by depth without this
    // silently files one under the other.
    let diverged = false;
    folders.forEach((folder, depth) => {
      diverged = diverged || previous[depth] !== folder;
      if (diverged) {
        rows.push({
          key: `${folders.slice(0, depth + 1).join('/')}/`,
          name: folder, depth, change: null,
        });
      }
    });
    previous = folders;
    rows.push({
      key: change.path, name: parts[parts.length - 1], depth: folders.length, change,
    });
  }
  return rows;
}

