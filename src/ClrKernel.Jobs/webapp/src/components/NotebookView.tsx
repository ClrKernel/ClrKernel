import DOMPurify from 'dompurify';
import Markdown from 'react-markdown';
import {
  isInjectedParameters,
  joinSource,
  parseAnsi,
  renderOutput,
  type Notebook,
  type NotebookCell,
  type NotebookOutput,
} from '../ipynb';

/** Text with ANSI colour codes, as styled spans. */
function AnsiText({ text }: { text: string }) {
  return (
    <pre className="output-text">
      {parseAnsi(text).map((span, i) => (
        <span key={i} className={span.className}>
          {span.text}
        </span>
      ))}
    </pre>
  );
}

/**
 * Rich output from the kernel's formatters. The HTML comes from our own kernel,
 * but notebooks can emit arbitrary HTML deliberately, so it is sanitised before
 * it goes anywhere near the DOM.
 */
function HtmlOutput({ html }: { html: string }) {
  const clean = DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
  return <div className="output-html" dangerouslySetInnerHTML={{ __html: clean }} />;
}

/** One kernel output. Shared with the editor's live results, so a cell run in
 *  the browser renders exactly the way the executed artifact will. */
export function Output({ output }: { output: NotebookOutput }) {
  const rendered = renderOutput(output);
  if (!rendered) {
    return null;
  }
  if (rendered.kind === 'html') {
    return <HtmlOutput html={rendered.html} />;
  }
  if (rendered.kind === 'text') {
    return <AnsiText text={rendered.text} />;
  }
  return (
    <div className="output-error">
      <strong>
        {rendered.ename}: {rendered.evalue}
      </strong>
      {rendered.traceback && <pre className="output-text">{rendered.traceback}</pre>}
    </div>
  );
}

function Cell({ cell }: { cell: NotebookCell }) {
  const source = joinSource(cell.source);
  if (cell.cell_type === 'markdown') {
    return (
      <div className="cell cell-markdown">
        <Markdown>{source}</Markdown>
      </div>
    );
  }

  const injected = isInjectedParameters(cell);
  return (
    <div className={`cell cell-code${injected ? ' cell-injected' : ''}`}>
      <div className="cell-gutter">{cell.execution_count ?? ' '}</div>
      <div className="cell-body">
        {injected && <div className="cell-tag">injected parameters</div>}
        <pre className="cell-source">{source}</pre>
        {(cell.outputs ?? []).map((output, i) => (
          <Output key={i} output={output} />
        ))}
      </div>
    </div>
  );
}

/** An executed .ipynb artifact, rendered cell by cell. */
export function NotebookView({ notebook }: { notebook: Notebook }) {
  const cells = notebook.cells ?? [];
  if (cells.length === 0) {
    return <p className="muted">This artifact has no cells.</p>;
  }
  return (
    <div className="notebook">
      {cells.map((cell, i) => (
        <Cell key={i} cell={cell} />
      ))}
    </div>
  );
}
