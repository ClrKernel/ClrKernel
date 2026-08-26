import * as monaco from 'monaco-editor';
import EditorWorker from './editor.worker?worker';
import { FONT_MONO, paletteFor, type ThemeName } from '../theme/palette';

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
 * Two themes, built from the same shape by the function below, so the dark one
 * cannot be missing a rule the light one has. `defineTheme`/`setTheme` are
 * global rather than per-editor, which is why there is nothing here to vary per
 * cell — and why switching is one call rather than a walk over every editor.
 *
 * The accent still never appears inside the editor (cursor and selection stay
 * neutral), so changing accent needs no re-theme. Changing *theme* does.
 */
function defineEditorTheme(name: string, theme: ThemeName): void {
  const editor = paletteFor(theme).editor;
  const neutral = paletteFor(theme).neutral;
  monaco.editor.defineTheme(name, {
    base: theme === 'dark' ? 'vs-dark' : 'vs',
    inherit: true,
    /* Warm Paper gives four syntax hues. Monaco's base themes assign their own,
       so these are re-stated against the standard token scopes rather than left
       to inherit — otherwise a C# cell renders in VS blue-and-red on cream. */
    rules: [
      /* The catch-all first: the base paints anything it has no rule for —
         operators, delimiters, punctuation — in its own foreground, which is
         neither the code colour nor anything in this palette. Later rules win,
         so the specific hues below still apply. */
      { token: '', foreground: editor.foreground.slice(1) },
      { token: 'delimiter', foreground: editor.foreground.slice(1) },
      { token: 'operator', foreground: editor.foreground.slice(1) },
      { token: 'identifier', foreground: editor.foreground.slice(1) },
      { token: 'keyword', foreground: editor.keyword.slice(1) },
      { token: 'string', foreground: editor.string.slice(1) },
      { token: 'number', foreground: editor.number.slice(1) },
      { token: 'comment', foreground: editor.comment.slice(1) },
      { token: 'annotation', foreground: editor.directive.slice(1) },
      { token: 'metatag', foreground: editor.directive.slice(1) },
      { token: 'keyword.directive', foreground: editor.directive.slice(1) },
      { token: 'type', foreground: editor.keyword.slice(1) },
    ],
    colors: {
      'editor.background': editor.background,
      'editor.foreground': editor.foreground,
      'editor.lineHighlightBackground': editor.lineHighlight,
      'editorLineNumber.foreground': editor.lineNumber,
      'editorIndentGuide.background1': editor.indentGuide,
      'editor.selectionBackground': editor.selection,
      'editorWidget.background': editor.widgetBackground,
      'editorWidget.border': editor.widgetBorder,
      /* Bracket-pair colourization ships on, and its defaults are Monaco's own
         hard-coded blue/orange/purple — three more hues than this palette has,
         and they clash on cream. Neutralised here rather than through the
         `bracketPairColorization` editor option, which is per-editor and did not
         take; the theme is global and does. */
      'editorBracketHighlight.foreground1': editor.foreground,
      'editorBracketHighlight.foreground2': editor.foreground,
      'editorBracketHighlight.foreground3': editor.foreground,
      'editorBracketHighlight.foreground4': editor.foreground,
      'editorBracketHighlight.foreground5': editor.foreground,
      'editorBracketHighlight.foreground6': editor.foreground,
      'editorBracketHighlight.unexpectedBracket.foreground': neutral.destructive,
    },
  });
}

defineEditorTheme('clrkernel-light', 'light');
defineEditorTheme('clrkernel-dark', 'dark');

/** The Monaco theme name for a resolved app theme. Exported because the
 *  thumbnail colorizer keys its cache on it: the colours are baked into the
 *  HTML `colorize` returns, so a theme change has to bust that cache. */
export function monacoThemeFor(theme: ThemeName): string {
  return theme === 'dark' ? 'clrkernel-dark' : 'clrkernel-light';
}

/** Global, like `defineTheme` — one call re-themes every editor on the page. */
export function applyEditorTheme(theme: ThemeName): void {
  monaco.editor.setTheme(monacoThemeFor(theme));
}

applyEditorTheme('light');

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
