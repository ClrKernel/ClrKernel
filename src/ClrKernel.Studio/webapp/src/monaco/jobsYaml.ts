import { monaco } from './setup';
import { completionsAt, type JobsSchemaDocument } from '../jobsSchema';

/**
 * Completion for `*.jobs.yaml`, from the schema the server publishes.
 *
 * Not `monaco-yaml`. That package is the obvious answer and does not work here:
 * it reaches Monaco through `monaco-worker-manager`, which calls
 * `createWebWorker({ moduleId, label })` — an API monaco-editor 0.56 replaced
 * with `{ worker }`. The worker is never created, every request fails with
 * "Missing requestHandler", and 5.5.1 is the latest release. Its `>=0.36` peer
 * range is simply wrong.
 *
 * So: completion here, and errors from the server, which builds this same schema
 * and is the authority the push gate uses anyway. What is lost against a real
 * language service is that errors arrive on save rather than on keystroke —
 * about a second behind. Worth revisiting if monaco-yaml catches up.
 *
 * Registered once for the whole `yaml` language, because Monaco's providers are
 * per-language and global; the provider itself declines any model that is not a
 * jobs file, so somebody's docker-compose gets no opinions from us.
 */
let registered: monaco.IDisposable | null = null;

export function registerJobsYaml(schema: JobsSchemaDocument): void {
  registered?.dispose();
  registered = monaco.languages.registerCompletionItemProvider('yaml', {
    // A letter starts a key; the list is short enough to offer eagerly.
    triggerCharacters: [' ', '-'],
    provideCompletionItems(model, position) {
      if (!model.uri.path.toLowerCase().endsWith('.jobs.yaml')) {
        return { suggestions: [] };
      }
      const line = model.getLineContent(position.lineNumber);
      const before = line.slice(0, position.column - 1);
      const word = model.getWordUntilPosition(position);
      const fields = completionsAt(
        schema,
        model.getLinesContent().slice(0, position.lineNumber - 1),
        before,
      );
      return {
        suggestions: fields.map((field) => ({
          label: field.name,
          kind: monaco.languages.CompletionItemKind.Property,
          insertText: `${field.name}: `,
          detail: field.type,
          documentation: field.description
            + (field.example ? `\n\nFor example: \`${field.example}\`` : ''),
          range: new monaco.Range(
            position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
        })),
      };
    },
  });
}
