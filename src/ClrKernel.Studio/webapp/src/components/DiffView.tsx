import { useDiffEditor } from '../monaco/useMonaco';

/**
 * Two texts side by side — the same view VS Code gives a branch comparison,
 * rather than a unified diff to read in your head.
 *
 * Shared rather than private to the editor because there are two comparisons
 * worth making about a file and they should be read the same way: what differs
 * between branches, and what one commit changed.
 */
export function DiffView({
  original, modified, language,
}: {
  original: string;
  modified: string;
  language: string;
}) {
  const container = useDiffEditor(original, modified, language, true);
  return <div className="diff-editor" ref={container} />;
}
