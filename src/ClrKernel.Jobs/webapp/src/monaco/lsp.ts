import type * as monacoType from 'monaco-editor';

/**
 * LSP payloads → Monaco's shapes. Kept free of Monaco imports and of the network
 * so it can be tested directly: the kernel speaks LSP, Monaco speaks its own
 * dialect, and every mistranslation between them shows up as an editor that is
 * subtly wrong rather than one that fails.
 *
 * `monaco.languages.CompletionItemKind` and LSP's CompletionItemKind are both
 * enums of the same concepts in a different order — this is the one place that
 * knows that.
 */

export interface LspPosition {
  line: number;
  character: number;
}

export interface LspRange {
  start: LspPosition;
  end: LspPosition;
}

export interface LspCompletionItem {
  label: string;
  kind?: number;
  detail?: string;
  documentation?: { kind?: string; value?: string } | string;
  insertText?: string;
  sortText?: string;
  filterText?: string;
  textEdit?: { range: LspRange; newText: string };
  data?: unknown;
}

export interface LspCompletionList {
  isIncomplete?: boolean;
  items?: LspCompletionItem[];
}

export interface LspHover {
  contents?: { kind?: string; value?: string } | string;
  range?: LspRange;
}

export interface LspSignatureHelp {
  signatures?: {
    label: string;
    documentation?: { kind?: string; value?: string } | string;
    parameters?: { label: string; documentation?: { value?: string } | string }[];
  }[];
  activeSignature?: number;
  activeParameter?: number;
}

/**
 * LSP's CompletionItemKind, which is 1-based and fixed by the spec, to the name
 * Monaco calls the same concept.
 *
 * Deliberately not a number-to-number table. Monaco's enum is an implementation
 * detail that moves — in the version bundled today `Snippet` is 28, and it was 27
 * before a `Tool` kind was inserted ahead of it. A table of numbers would keep
 * building and quietly show the wrong icons; a name is resolved against the real
 * enum at the call site, so a rename fails loudly instead.
 */
const KIND_NAMES: Record<number, string> = {
  1: 'Text',
  2: 'Method',
  3: 'Function',
  4: 'Constructor',
  5: 'Field',
  6: 'Variable',
  7: 'Class',
  8: 'Interface',
  9: 'Module',
  10: 'Property',
  11: 'Unit',
  12: 'Value',
  13: 'Enum',
  14: 'Keyword',
  15: 'Snippet',
  16: 'Color',
  17: 'File',
  18: 'Reference',
  19: 'Folder',
  20: 'EnumMember',
  21: 'Constant',
  22: 'Struct',
  23: 'Event',
  24: 'Operator',
  25: 'TypeParameter',
};

/**
 * The Monaco kind for an LSP kind, resolved against Monaco's own enum. Anything
 * unrecognised becomes Text: a plain icon beats a confidently wrong one.
 */
export function completionKind(lspKind: number | undefined, kinds: Record<string, number>): number {
  const name = lspKind != null ? KIND_NAMES[lspKind] : undefined;
  const resolved = name != null ? kinds[name] : undefined;
  return resolved ?? kinds.Text ?? 0;
}

/** Markdown either way, but LSP allows a bare string for older servers. */
export function markdown(
  content: { kind?: string; value?: string } | string | undefined | null,
): { value: string } | undefined {
  if (content == null) {
    return undefined;
  }
  const value = typeof content === 'string' ? content : (content.value ?? '');
  return value.length > 0 ? { value } : undefined;
}

/** LSP ranges are 0-based; Monaco counts lines and columns from 1. */
export function toMonacoRange(range: LspRange | undefined): monacoType.IRange | undefined {
  return range == null
    ? undefined
    : {
        startLineNumber: range.start.line + 1,
        startColumn: range.start.character + 1,
        endLineNumber: range.end.line + 1,
        endColumn: range.end.character + 1,
      };
}

/**
 * One completion item. The server sends a textEdit covering the word being
 * replaced, which is what makes completing halfway through an identifier replace
 * it rather than append to it — so the range is taken from there when present,
 * and only falls back to the word under the cursor when it is not.
 *
 * `data` is round-tripped untouched: it is how the server finds the item again on
 * resolve, and it encodes both which list the item came from and where in it.
 */
export function toMonacoCompletion(
  item: LspCompletionItem,
  fallback: monacoType.IRange,
  kinds: Record<string, number>,
): monacoType.languages.CompletionItem {
  const range = toMonacoRange(item.textEdit?.range) ?? fallback;
  return {
    label: item.label,
    kind: completionKind(item.kind, kinds) as monacoType.languages.CompletionItemKind,
    detail: item.detail,
    documentation: markdown(item.documentation),
    insertText: item.textEdit?.newText ?? item.insertText ?? item.label,
    sortText: item.sortText,
    filterText: item.filterText,
    range,
    // Not part of Monaco's model — carried so resolve can hand it back.
    _lsp: item,
  } as monacoType.languages.CompletionItem & { _lsp: LspCompletionItem };
}

export function toMonacoHover(hover: LspHover | null | undefined): monacoType.languages.Hover | null {
  const contents = markdown(hover?.contents);
  if (contents == null) {
    return null;
  }
  return { contents: [contents], range: toMonacoRange(hover?.range) };
}

export function toMonacoSignatureHelp(
  help: LspSignatureHelp | null | undefined,
): monacoType.languages.SignatureHelp | null {
  if (help?.signatures == null || help.signatures.length === 0) {
    return null;
  }
  return {
    signatures: help.signatures.map((signature) => ({
      label: signature.label,
      documentation: markdown(signature.documentation),
      parameters: (signature.parameters ?? []).map((parameter) => ({
        label: parameter.label,
        documentation: markdown(parameter.documentation),
      })),
    })),
    activeSignature: help.activeSignature ?? 0,
    activeParameter: help.activeParameter ?? 0,
  };
}
