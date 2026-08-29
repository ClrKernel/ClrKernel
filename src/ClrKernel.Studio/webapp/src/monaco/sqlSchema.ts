import { api } from '../api';
import { readSchema, writeSchema } from '../connectionCache';
import { suggestionsFor, type SqlSchema, type Suggestion } from '../sqlCompletion';
import { monaco } from './setup';

/**
 * Table and column completion in SQL editors, from the schema of whichever
 * connection the editor is pointed at.
 *
 * A second provider rather than an extension of the kernel-backed one in
 * `language.ts`: Monaco merges the results of every provider registered for a
 * language, and these two answer genuinely different questions. The kernel knows
 * the session — the variables a cell defined. The database knows the schema. A
 * notebook SQL cell wants both, and gets both without either having to know about
 * the other.
 */

/** Which connection an editor's model is querying, asked at completion time
 *  because the answer changes when somebody picks a different connection. */
type ConnectionOf = () => string | null;

/**
 * Models that are SQL editors pointed at a connection.
 *
 * Weak, and the same guard `language.ts` uses and for the same reason: a provider
 * is registered per language globally, so it fires for every `sql` model on the
 * page — including a notebook cell in a notebook nobody has connected. A model
 * nobody registered gets nothing back.
 */
const editors = new WeakMap<monaco.editor.ITextModel, ConnectionOf>();

export function bindSqlEditor(model: monaco.editor.ITextModel, connection: ConnectionOf): void {
  // The first editor to claim one registers the provider. Lazily, so nothing is
  // registered on a page with no SQL on it, and here rather than at module load so
  // there is no import cycle with setup.ts and no side effect hiding in an import.
  registerSqlSchemaCompletion();
  editors.set(model, connection);
}

export function unbindSqlEditor(model: monaco.editor.ITextModel): void {
  editors.delete(model);
}

/** In flight, so ten keystrokes do not become ten identical fetches. */
const loading = new Map<string, Promise<SqlSchema | null>>();

/**
 * The schema for a connection, from the tab's cache or from the server once.
 *
 * Failures are cached as "nothing", not retried per keystroke: a connection that
 * cannot be reached is not going to become reachable between two characters, and
 * a completion provider that reconnects on every letter is a denial of service
 * somebody typed by accident. Refresh or Disconnect clears it — the schema lives
 * under the same cache key as the connection's tree.
 */
async function schemaFor(connectionId: string): Promise<SqlSchema | null> {
  const cached = readSchema(connectionId);
  if (cached != null) {
    return cached;
  }
  const already = loading.get(connectionId);
  if (already != null) {
    return already;
  }
  const fetching = api
    .connectionMetadata<SqlSchema>(connectionId, { level: 'completions' })
    .then((reply) => {
      const schema = reply.supported && reply.error == null && reply.payload != null
        ? reply.payload
        : { database: '', objects: [], truncated: false };
      writeSchema(connectionId, schema);
      return schema;
    })
    .catch(() => {
      const nothing = { database: '', objects: [], truncated: false };
      writeSchema(connectionId, nothing);
      return nothing;
    })
    .finally(() => loading.delete(connectionId));
  loading.set(connectionId, fetching);
  return fetching;
}

let registered = false;

/**
 * Registers the provider, once. Monaco has no way to amend one afterwards, which
 * is the same constraint the kernel-backed provider lives with.
 */
function registerSqlSchemaCompletion(): void {
  if (registered) {
    return;
  }
  registered = true;

  monaco.languages.registerCompletionItemProvider('sql', {
    // The dot, because `alias.` is the moment this earns its keep and Monaco does
    // not ask again on its own after a non-word character.
    triggerCharacters: ['.'],
    provideCompletionItems: async (model, position) => {
      const connectionId = editors.get(model)?.();
      if (connectionId == null) {
        return { suggestions: [] };
      }
      const schema = await schemaFor(connectionId);
      if (schema == null || model.isDisposed()) {
        return { suggestions: [] };
      }
      const textToCursor = model.getValueInRange({
        startLineNumber: 1,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      });
      const found = suggestionsFor(model.getValue(), textToCursor, schema);
      if (found.length === 0) {
        return { suggestions: [] };
      }
      // Replace the word being typed rather than appending to it. After a dot the
      // word is empty and this is just the cursor, which is what we want there.
      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: word.endColumn,
      };
      return { suggestions: found.map((s, i) => toMonaco(s, i, range)) };
    },
  });
}

function toMonaco(
  suggestion: Suggestion, index: number, range: monaco.IRange,
): monaco.languages.CompletionItem {
  return {
    label: suggestion.label,
    kind: KINDS[suggestion.kind],
    detail: suggestion.detail,
    insertText: suggestion.label,
    range,
    // Monaco sorts on this text, so the rank has to be padded to sort as a number
    // would — and the index keeps a table's own column order inside one rank.
    sortText: `${suggestion.rank}${String(index).padStart(5, '0')}`,
  };
}

const KINDS: Record<Suggestion['kind'], monaco.languages.CompletionItemKind> = {
  schema: monaco.languages.CompletionItemKind.Module,
  table: monaco.languages.CompletionItemKind.Struct,
  view: monaco.languages.CompletionItemKind.Interface,
  column: monaco.languages.CompletionItemKind.Field,
};
