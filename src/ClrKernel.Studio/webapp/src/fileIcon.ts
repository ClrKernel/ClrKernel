/**
 * What a file's little badge says and what colour it is.
 *
 * React-free so it can be tested, which this earns: it is a table plus a
 * fallback, and the fallback is the half that decides what a file nobody
 * planned for looks like.
 *
 * Two or three characters rather than a drawn icon, the way Azure DevOps does
 * it. The tree only ever holds notebooks and `*.jobs.yaml` — NotebookTree filters
 * to those — so a full icon set would be a megabyte of SVG to draw five glyphs,
 * and the letters say more than a generic page-with-a-corner anyway.
 */

export type FileTone = 'notebook' | 'code' | 'config';

export interface FileBadge {
  /** What is printed in the badge. Two or three characters, never more. */
  label: string;
  tone: FileTone;
}

/**
 * Longest extension first — `.nb.md` has to be matched before `.md`, and
 * `.jobs.yaml` before `.yaml`, or the shorter one wins and every notebook in
 * the tree is labelled markdown.
 */
const KNOWN: { extension: string; label: string; tone: FileTone }[] = [
  { extension: '.jobs.yaml', label: 'JOB', tone: 'config' },
  { extension: '.nb.md', label: 'M↓', tone: 'notebook' },
  { extension: '.ipynb', label: 'NB', tone: 'notebook' },
  { extension: '.dib', label: 'DIB', tone: 'notebook' },
  { extension: '.csx', label: 'C#', tone: 'code' },
  { extension: '.cs', label: 'C#', tone: 'code' },
  { extension: '.yaml', label: 'YML', tone: 'config' },
  { extension: '.yml', label: 'YML', tone: 'config' },
  { extension: '.json', label: '{ }', tone: 'config' },
  { extension: '.md', label: 'M↓', tone: 'notebook' },
  { extension: '.sql', label: 'SQL', tone: 'code' },
  { extension: '.ps1', label: 'PS', tone: 'code' },
  { extension: '.sh', label: 'SH', tone: 'code' },
  { extension: '.py', label: 'PY', tone: 'code' },
  { extension: '.ts', label: 'TS', tone: 'code' },
  { extension: '.js', label: 'JS', tone: 'code' },
];

export function fileBadge(name: string): FileBadge {
  const lower = (name ?? '').toLowerCase();
  const known = KNOWN.find((k) => lower.endsWith(k.extension));
  if (known) {
    return { label: known.label, tone: known.tone };
  }
  // Anything else wears its own extension, so a file nobody thought about is
  // still told apart from the one beside it. No extension — a dotfile, or a
  // bare LICENSE — falls back to the name, because an empty box says less than
  // the wrong three letters.
  const dot = lower.lastIndexOf('.');
  const source = dot > 0 ? lower.slice(dot + 1) : lower.replace(/^\.+/, '');
  return { label: (source.slice(0, 3) || '?').toUpperCase(), tone: 'code' };
}
