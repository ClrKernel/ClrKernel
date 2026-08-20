import { useEffect, useRef } from 'react';
import { cellEditorOptions, monaco } from './setup';

/** Tallest a single cell grows before it scrolls internally. */
const MAX_CELL_HEIGHT = 600;

/**
 * A Monaco editor for one notebook cell: it sizes itself to its content, swaps
 * language without losing undo history, and disposes cleanly.
 *
 * Disposing the *model* as well as the editor matters — Monaco models are global
 * and outlive the editor that showed them, so a notebook scrolled through a few
 * times would otherwise leak one per cell.
 */
export function useCellEditor(
  language: string,
  value: string,
  onChange: (value: string) => void,
  readOnly = false,
) {
  const container = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const latestOnChange = useRef(onChange);
  latestOnChange.current = onChange;

  useEffect(() => {
    if (!container.current) {
      return;
    }
    const created = monaco.editor.create(container.current, {
      ...cellEditorOptions,
      value,
      language,
      readOnly,
    });
    editor.current = created;

    const resize = () => {
      const height = Math.min(created.getContentHeight(), MAX_CELL_HEIGHT);
      if (container.current) {
        container.current.style.height = `${Math.max(height, 32)}px`;
      }
      created.layout();
    };
    resize();

    const sizeListener = created.onDidContentSizeChange(resize);
    const changeListener = created.onDidChangeModelContent(() =>
      latestOnChange.current(created.getValue()),
    );

    return () => {
      sizeListener.dispose();
      changeListener.dispose();
      created.getModel()?.dispose();
      created.dispose();
      editor.current = null;
    };
    // Created once per cell: value and language are applied below so typing
    // never rebuilds the editor (which would drop the cursor and undo stack).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Language changes when the picker moves; setModelLanguage keeps the model,
  // so undo history and the cursor survive.
  useEffect(() => {
    const model = editor.current?.getModel();
    if (model) {
      monaco.editor.setModelLanguage(model, language);
    }
  }, [language]);

  // Only for value changes that came from outside this editor (a reload, or a
  // cell moving) — writing back what the user just typed would fight the cursor.
  useEffect(() => {
    const current = editor.current;
    if (current && current.getValue() !== value) {
      current.setValue(value);
    }
  }, [value]);

  return container;
}

/**
 * A read-only side-by-side diff. Used for "Diff vs production", where the two
 * sides are the same notebook on the two branches.
 */
export function useDiffEditor(original: string, modified: string, language: string) {
  const container = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!container.current) {
      return;
    }
    const editor = monaco.editor.createDiffEditor(container.current, {
      automaticLayout: true,
      readOnly: true,
      renderSideBySide: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 13,
    });
    const originalModel = monaco.editor.createModel(original, language);
    const modifiedModel = monaco.editor.createModel(modified, language);
    editor.setModel({ original: originalModel, modified: modifiedModel });

    return () => {
      originalModel.dispose();
      modifiedModel.dispose();
      editor.dispose();
    };
  }, [original, modified, language]);

  return container;
}
