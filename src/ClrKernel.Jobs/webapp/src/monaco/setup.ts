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
  rules: [],
  colors: {
    'editor.background': EDITOR.background,
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
  fontSize: 14,
  fontFamily: FONT_MONO,
  padding: { top: 8, bottom: 8 },
  renderLineHighlight: 'none',
  fixedOverflowWidgets: true,
};

export { monaco };
