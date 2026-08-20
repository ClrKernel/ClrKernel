import * as monaco from 'monaco-editor';
import EditorWorker from './editor.worker?worker';

/**
 * Monaco, bundled locally. The worker is resolved through `new URL(…,
 * import.meta.url)` so Vite emits it as an asset of our own build: the editor
 * works offline and inside the container, where Monaco's default CDN loader
 * would simply fail.
 *
 * One worker serves every language: we ship no language services (no IntelliSense
 * from the kernel yet), so the TypeScript/JSON/CSS/HTML workers would be dead
 * weight. Syntax highlighting is tokenizer-only and needs no worker at all.
 */
self.MonacoEnvironment = { getWorker: () => new EditorWorker() };

const darkMode = window.matchMedia?.('(prefers-color-scheme: dark)');

function applyTheme(): void {
  monaco.editor.setTheme(darkMode?.matches ? 'vs-dark' : 'vs');
}

// The app has no theme toggle: it follows the OS, so the editor does too.
darkMode?.addEventListener?.('change', applyTheme);
applyTheme();

/** Editor options shared by every cell — a notebook cell is not a file window. */
export const cellEditorOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
  automaticLayout: true,
  minimap: { enabled: false },
  lineNumbers: 'off',
  glyphMargin: false,
  folding: false,
  lineDecorationsWidth: 8,
  lineNumbersMinChars: 0,
  overviewRulerLanes: 0,
  scrollBeyondLastLine: false,
  scrollbar: { alwaysConsumeMouseWheel: false, vertical: 'auto' },
  wordWrap: 'on',
  fontSize: 13,
  padding: { top: 8, bottom: 8 },
  renderLineHighlight: 'none',
  fixedOverflowWidgets: true,
};

export { monaco };
