import { fileBadge, type FileTone } from '../fileIcon';

/** Which token paints a kind of file. Named, not computed, so the linter that
 *  keeps colour in the token layer can see them. */
const TONE: Record<FileTone, string> = {
  notebook: 'text-file-notebook',
  code: 'text-file-code',
  config: 'text-file-config',
};

/**
 * A file's extension as a small badge, the way Azure DevOps writes `C#` and
 * `M↓` rather than drawing a page with a folded corner.
 *
 * Outlined in its own colour rather than filled: the explorer row already
 * changes background on hover and again when selected, and a filled chip has to
 * fight both of those to stay legible.
 */
export function FileBadge({ name }: { name: string }) {
  const { label, tone } = fileBadge(name);
  return (
    <span
      aria-hidden="true"
      title={label}
      className={[
        'inline-flex h-3.5 min-w-[19px] shrink-0 items-center justify-center rounded-[3px]',
        'border border-current/40 px-[2px] font-mono text-[8px] font-bold leading-none',
        TONE[tone],
      ].join(' ')}
    >
      {label}
    </span>
  );
}
