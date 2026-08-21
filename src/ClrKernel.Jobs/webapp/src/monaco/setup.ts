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

// Monaco's stock themes paint their own background, which reads as a white box
// dropped into the page. These inherit the app's palette so a cell looks like
// part of the notebook rather than an embedded IDE.
monaco.editor.defineTheme('clrkernel', {
  base: 'vs',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#ffffff',
    'editor.lineHighlightBackground': '#f6f7f9',
    'editorLineNumber.foreground': '#9aa3b0',
    'editorIndentGuide.background1': '#eef0f3',
    'editor.selectionBackground': '#cfe0ff',
    'editorWidget.background': '#ffffff',
    'editorWidget.border': '#d8dce3',
  },
});

monaco.editor.defineTheme('clrkernel-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#14171c',
    'editor.lineHighlightBackground': '#1c2027',
    'editorLineNumber.foreground': '#6b7280',
    'editorIndentGuide.background1': '#242a33',
    'editor.selectionBackground': '#2f4468',
    'editorWidget.background': '#1c2027',
    'editorWidget.border': '#2c323c',
  },
});

function applyTheme(): void {
  monaco.editor.setTheme(darkMode?.matches ? 'clrkernel-dark' : 'clrkernel');
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
