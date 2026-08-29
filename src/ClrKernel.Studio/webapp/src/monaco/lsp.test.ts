import { describe, expect, it } from 'vitest';
import {
  completionKind,
  definitionTarget,
  markdown,
  toMonacoCompletion,
  toMonacoHover,
  toMonacoMarker,
  toMonacoRange,
  toMonacoSignatureHelp,
} from './lsp';

// Monaco's CompletionItemKind, as the bundled version numbers it. Only the values
// the assertions below name; the point is that the mapping goes through names.
const kinds: Record<string, number> = {
  Method: 0,
  Function: 1,
  Variable: 4,
  Class: 5,
  Keyword: 17,
  Text: 18,
  Snippet: 28,
};

const range = { start: { line: 2, character: 4 }, end: { line: 2, character: 9 } };

describe('completionKind', () => {
  it('maps by name, so Monaco renumbering its enum cannot silently misdraw icons', () => {
    expect(completionKind(2, kinds)).toBe(kinds.Method);
    expect(completionKind(6, kinds)).toBe(kinds.Variable);
    expect(completionKind(7, kinds)).toBe(kinds.Class);
    // LSP 15 is Snippet, which is 28 here and was 27 before a kind was inserted
    // ahead of it. A number-to-number table would have kept building and been wrong.
    expect(completionKind(15, kinds)).toBe(kinds.Snippet);
  });

  it('falls back to Text rather than to whatever sits at that index', () => {
    expect(completionKind(999, kinds)).toBe(kinds.Text);
    expect(completionKind(undefined, kinds)).toBe(kinds.Text);
  });
});

describe('toMonacoRange', () => {
  it('shifts from LSP zero-based to Monaco one-based', () => {
    // Off by one here is off by one everywhere: completions replace the wrong
    // span and hovers underline the neighbouring token.
    expect(toMonacoRange(range)).toEqual({
      startLineNumber: 3,
      startColumn: 5,
      endLineNumber: 3,
      endColumn: 10,
    });
  });
});

describe('toMonacoCompletion', () => {
  const fallback = { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 };

  it('takes its range from the server textEdit, which is the word being replaced', () => {
    // Completing halfway through an identifier has to replace it. Using the
    // fallback would insert alongside what is already typed — "ConConsole".
    const item = toMonacoCompletion(
      { label: 'WriteLine', kind: 2, textEdit: { range, newText: 'WriteLine' } },
      fallback,
      kinds,
    );
    expect(item.range).toEqual(toMonacoRange(range));
    expect(item.insertText).toBe('WriteLine');
  });

  it('falls back to the word under the cursor when the server sends no edit', () => {
    expect(toMonacoCompletion({ label: 'x' }, fallback, kinds).range).toEqual(fallback);
  });

  it('carries the item back untouched, which is how resolve finds it again', () => {
    // data encodes both which completion list the item came from and where in it,
    // so a resolve queued behind a newer list cannot serve another symbol's docs.
    const original = { label: 'WriteLine', data: '7:3:/tmp/nb.md' };
    const item = toMonacoCompletion(original, fallback, kinds) as { _lsp?: unknown };
    expect(item._lsp).toBe(original);
  });
});

describe('toMonacoMarker', () => {
  // Monaco's MarkerSeverity is a bit-flag set; LSP's is a plain ordinal.
  const severities = { Hint: 1, Info: 2, Warning: 4, Error: 8 };

  it('maps severities by name, because the two scales collide on 1 and 2', () => {
    // LSP 1 is Error and Monaco 1 is Hint; LSP 2 is Warning and Monaco 2 is Info.
    // A number-to-number table would look plausible and show every SQL syntax
    // error as a faint grey hint.
    expect(toMonacoMarker({ range, message: 'x', severity: 1 }, severities).severity).toBe(severities.Error);
    expect(toMonacoMarker({ range, message: 'x', severity: 2 }, severities).severity).toBe(severities.Warning);
    expect(toMonacoMarker({ range, message: 'x', severity: 3 }, severities).severity).toBe(severities.Info);
    expect(toMonacoMarker({ range, message: 'x', severity: 4 }, severities).severity).toBe(severities.Hint);
  });

  it('treats an unstated severity as an error rather than a hint', () => {
    expect(toMonacoMarker({ range, message: 'x' }, severities).severity).toBe(severities.Error);
  });

  it('carries the message, source and code, and shifts the range', () => {
    const marker = toMonacoMarker(
      { range, message: 'Incorrect syntax near ;', source: 'clrkernel-sql', code: 46010 },
      severities,
    );
    expect(marker.message).toBe('Incorrect syntax near ;');
    expect(marker.source).toBe('clrkernel-sql');
    expect(marker.code).toBe('46010');
    expect(marker.startLineNumber).toBe(3);
    expect(marker.startColumn).toBe(5);
  });
});

describe('definitionTarget', () => {
  it('reads the cell from the fragment and ignores the path', () => {
    // The path in a cell URI is the kernel's absolute path to the notebook, which
    // the browser never learns. It does not need to: a session only answers about
    // its own notebook, so the fragment alone identifies the cell.
    expect(definitionTarget('vscode-notebook-cell:/srv/notebooks/test/etl.nb.md#c3'))
      .toEqual({ kind: 'cell', cellId: 'c3' });
    expect(definitionTarget('vscode-notebook-cell:/C:/work/etl.nb.md#c11'))
      .toEqual({ kind: 'cell', cellId: 'c11' });
  });

  it('reads a decompiled symbol by its key', () => {
    expect(definitionTarget('clrkernel-metadata:/System.Console'))
      .toEqual({ kind: 'metadata', key: 'System.Console' });
    // Keys with generics arrive percent-encoded.
    expect(definitionTarget('clrkernel-metadata:/System.Collections.Generic.List%601'))
      .toEqual({ kind: 'metadata', key: 'System.Collections.Generic.List`1' });
  });

  it('refuses to guess at anything else', () => {
    // A target with no fragment and no known scheme has nowhere to go; returning
    // a wrong cell id would peek at unrelated code.
    expect(definitionTarget('file:///etc/passwd')).toEqual({ kind: 'unknown' });
    expect(definitionTarget('')).toEqual({ kind: 'unknown' });
    expect(definitionTarget(null)).toEqual({ kind: 'unknown' });
  });
});

describe('toMonacoHover and toMonacoSignatureHelp', () => {
  it('says nothing rather than showing an empty popup', () => {
    // The blank hover box is worse than no hover box.
    expect(toMonacoHover(null)).toBeNull();
    expect(toMonacoHover({ contents: { value: '' } })).toBeNull();
    expect(toMonacoSignatureHelp(null)).toBeNull();
    expect(toMonacoSignatureHelp({ signatures: [] })).toBeNull();
  });

  it('reads a MarkupContent or the bare string an older server may send', () => {
    expect(markdown({ kind: 'markdown', value: 'text' })).toEqual({ value: 'text' });
    expect(markdown('text')).toEqual({ value: 'text' });
    expect(markdown(undefined)).toBeUndefined();
  });

  it('keeps the signature label and its active parameter', () => {
    // This is the "void Console.WriteLine(bool value)" popup.
    const help = toMonacoSignatureHelp({
      signatures: [{
        label: 'void Console.WriteLine(bool value)',
        parameters: [{ label: 'bool value' }],
      }],
      activeSignature: 0,
      activeParameter: 0,
    });
    expect(help?.signatures[0].label).toBe('void Console.WriteLine(bool value)');
    expect(help?.signatures[0].parameters[0].label).toBe('bool value');
    expect(help?.activeParameter).toBe(0);
  });
});
