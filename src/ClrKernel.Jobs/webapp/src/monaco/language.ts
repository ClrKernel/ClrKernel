import { api } from '../api';
import {
  toMonacoCompletion,
  toMonacoHover,
  toMonacoSignatureHelp,
  type LspCompletionItem,
  type LspCompletionList,
  type LspHover,
  type LspSignatureHelp,
} from './lsp';
import { monaco } from './setup';

/**
 * IntelliSense for notebook cells, answered by the cell's own kernel.
 *
 * The kernel is `clrkernel lsp` — the same server the VS Code extension talks to,
 * so a completion here is the completion there, and it sees the live REPL: a
 * variable a cell defined is a variable the next cell can complete against.
 */

/** Which cell a Monaco model belongs to, if any. */
interface CellRef {
  path: string;
  cellId: string;
  /** The kernel's name for the language — never Monaco's. Read at request time
   *  because the picker can change it without the model being replaced. */
  languageId: () => string;
  /** False for a language the kernel has no editor services for; asking would
   *  cost a round trip to be told nothing. */
  enabled: () => boolean;
}

/**
 * Monaco's providers are registered per *language*, globally — a `csharp`
 * provider fires for the Source tab showing a `.csx` file and for both sides of
 * the production diff, neither of which is a cell. This map is the guard: a model
 * nobody registered is not a cell, and every provider returns immediately.
 *
 * Weak, so a disposed cell's model does not pin its entry.
 */
const cells = new WeakMap<monaco.editor.ITextModel, CellRef>();

export function bindCell(model: monaco.editor.ITextModel, ref: CellRef): void {
  cells.set(model, ref);
}

/**
 * The Monaco languages a cell can be shown in — the range of `monacoLanguage()`.
 * Registering for all of them and letting the server decide is simpler than
 * deriving the set from the kernel's languages, and it is also more correct: C#
 * cells are the main case and C# is not a registered cell language at all, so a
 * descriptor-driven list would leave out the one that matters most.
 */
const LANGUAGES = ['csharp', 'sql', 'powershell', 'shell', 'plaintext'];

let registered = false;

/**
 * Triggers come from the kernel's handshake, so the editor asks on exactly the
 * characters the server says it answers on. Registration happens once, on the
 * first session that reports them — Monaco has no way to amend a provider's
 * trigger characters afterwards, which is the same constraint the VS Code client
 * lives with (its document selector is fixed at construction too).
 */
export function registerLanguageProviders(
  completionTriggers: string[] = [],
  signatureTriggers: string[] = [],
): void {
  if (registered) {
    return;
  }
  registered = true;

  for (const language of LANGUAGES) {
    monaco.languages.registerCompletionItemProvider(language, {
      triggerCharacters: completionTriggers,
      provideCompletionItems: async (model, position) => {
        const ref = cells.get(model);
        if (ref == null || !ref.enabled()) {
          return { suggestions: [] };
        }
        const list = await ask<LspCompletionList>(ref, 'completion', model, position);
        if (list?.items == null) {
          return { suggestions: [] };
        }
        // Where a completion goes when the server sends no textEdit: the word
        // being typed, so accepting one replaces it rather than doubling it.
        const word = model.getWordUntilPosition(position);
        const fallback = {
          startLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endLineNumber: position.lineNumber,
          endColumn: word.endColumn,
        };
        return {
          incomplete: list.isIncomplete ?? false,
          suggestions: list.items.map((item) =>
            toMonacoCompletion(item, fallback, monaco.languages.CompletionItemKind as never),
          ),
        };
      },
      // Where "void Console.WriteLine(bool value)" actually comes from: the list
      // carries labels, the documentation is fetched for the focused item only.
      resolveCompletionItem: async (item) => {
        const original = (item as { _lsp?: LspCompletionItem })._lsp;
        const ref = lastCell;
        if (original == null || ref == null) {
          return item;
        }
        try {
          const resolved = await api.languageRequest<LspCompletionItem>(ref.path, {
            kind: 'resolve',
            cellId: ref.cellId,
            languageId: ref.languageId(),
            source: '',
            line: 0,
            character: 0,
            item: original,
          });
          return resolved?.documentation == null
            ? item
            : { ...item, documentation: { value: documentationText(resolved.documentation) } };
        } catch {
          return item; // documentation is cosmetic; never break the list over it
        }
      },
    });

    monaco.languages.registerHoverProvider(language, {
      provideHover: async (model, position) => {
        const ref = cells.get(model);
        if (ref == null || !ref.enabled()) {
          return null;
        }
        return toMonacoHover(await ask<LspHover>(ref, 'hover', model, position));
      },
    });

    monaco.languages.registerSignatureHelpProvider(language, {
      signatureHelpTriggerCharacters: signatureTriggers,
      provideSignatureHelp: async (model, position) => {
        const ref = cells.get(model);
        if (ref == null || !ref.enabled()) {
          return null;
        }
        const help = toMonacoSignatureHelp(
          await ask<LspSignatureHelp>(ref, 'signatureHelp', model, position),
        );
        return help == null ? null : { value: help, dispose: () => undefined };
      },
    });
  }
}

/**
 * The cell whose completion list is on screen. Monaco's resolveCompletionItem is
 * handed the item and nothing else — no model, no position — so the cell has to
 * be remembered from the request that produced the list. There is only ever one
 * suggest widget open, which is what makes a single slot correct here.
 */
let lastCell: CellRef | null = null;

async function ask<T>(
  ref: CellRef,
  kind: 'completion' | 'hover' | 'signatureHelp',
  model: monaco.editor.ITextModel,
  position: monaco.Position,
): Promise<T | null> {
  lastCell = ref;
  try {
    return await api.languageRequest<T>(ref.path, {
      kind,
      cellId: ref.cellId,
      languageId: ref.languageId(),
      // The cell's text travels with the question. The background sync is
      // debounced and a keystroke cannot wait for it — a position measured
      // against text that is even slightly behind answers about the wrong symbol.
      source: model.getValue(),
      // LSP counts from zero; Monaco counts from one.
      line: position.lineNumber - 1,
      character: position.column - 1,
    });
  } catch {
    // A language feature that cannot answer says nothing. It must never surface
    // as an error banner over someone's typing.
    return null;
  }
}

function documentationText(documentation: LspCompletionItem['documentation']): string {
  return typeof documentation === 'string' ? documentation : (documentation?.value ?? '');
}
