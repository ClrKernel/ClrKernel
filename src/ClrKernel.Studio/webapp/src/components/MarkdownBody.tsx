import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

/**
 * Rendered markdown, everywhere it is rendered.
 *
 * One component rather than three `<Markdown>` calls, because the plugins and
 * the class have to be the same in all of them: a cell's preview, Focus Mode's
 * output pane, a finished run's artifact, and a `.md` file's Preview tab are four
 * views of the same kind of document, and a table that renders in one and comes
 * out as pipe soup in another is the bug this replaced.
 *
 * `remark-gfm` is what makes tables, task lists, strikethrough and bare URLs work
 * — CommonMark has none of them, and a `.md` in a project is written by someone
 * who has been using GitHub. `.markdown-body` is the typography: Tailwind's
 * preflight zeroes a document's spacing, so it has to be put back.
 */
export function MarkdownBody({ children }: { children: string }) {
  return (
    <div className="markdown-body">
      <Markdown remarkPlugins={[remarkGfm]}>{children}</Markdown>
    </div>
  );
}
