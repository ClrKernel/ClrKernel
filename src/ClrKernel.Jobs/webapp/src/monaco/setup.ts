import * as monaco from 'monaco-editor';
import EditorWorker from './editor.worker?worker';
import { EDITOR, FONT_MONO } from '../theme/palette';

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

/**
 * One theme, defined from the shared palette so the editor's background is the
 * same value as the `--code-bg` token — anything else leaves a visible seam at
 * the cell's edge.
 *
 * Light only, on purpose. `defineTheme`/`setTheme` are global rather than
 * per-editor anyway, so there is nothing here to vary per cell. The accent
 * never appears inside the editor (cursor and selection stay neutral), so
 * changing themes needs no re-theme.
 */
monaco.editor.defineTheme('clrkernel-light', {
  base: 'vs',
  inherit: true,
  /* Warm Paper gives four syntax hues. Monaco's `vs` base assigns its own, so
     these are re-stated against the standard token scopes rather than left to
     inherit — otherwise a C# cell renders in VS blue-and-red on cream. */
  rules: [
    { token: 'keyword', foreground: EDITOR.keyword.slice(1) },
    { token: 'string', foreground: EDITOR.string.slice(1) },
    { token: 'number', foreground: EDITOR.number.slice(1) },
    { token: 'comment', foreground: EDITOR.comment.slice(1) },
    { token: 'annotation', foreground: EDITOR.directive.slice(1) },
    { token: 'metatag', foreground: EDITOR.directive.slice(1) },
    { token: 'keyword.directive', foreground: EDITOR.directive.slice(1) },
    { token: 'type', foreground: EDITOR.keyword.slice(1) },
  ],
  colors: {
    'editor.background': EDITOR.background,
    'editor.foreground': EDITOR.foreground,
    'editor.lineHighlightBackground': EDITOR.lineHighlight,
    'editorLineNumber.foreground': EDITOR.lineNumber,
    'editorIndentGuide.background1': EDITOR.indentGuide,
    'editor.selectionBackground': EDITOR.selection,
    'editorWidget.background': EDITOR.widgetBackground,
    'editorWidget.border': EDITOR.widgetBorder,
  },
});

monaco.editor.setTheme('clrkernel-light');

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
  /* 12.5px is the design's code size. Monaco takes a number, not a token. */
  fontSize: 12.5,
  fontFamily: FONT_MONO,
  padding: { top: 8, bottom: 8 },
  renderLineHighlight: 'none',
  fixedOverflowWidgets: true,
};

export { monaco };
