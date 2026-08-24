import { useCallback, useEffect, useRef } from 'react';
import { bindCell } from './language';
import { toMonacoMarker, type LspDiagnostic } from './lsp';
import { getCellModel } from './models';
import { cellEditorOptions, monaco } from './setup';

/** Identifies a cell to the language providers. Absent for editors that are not
 *  cells — the Source tab, the production diff — which is what keeps completion
 *  from firing on them. */
export interface CellBinding {
  path: string;
  cellId: string;
  /** The kernel's language id, not Monaco's. */
  languageId: string;
  /** False when the kernel has no editor services for this language. */
  enabled: boolean;
  /** What the kernel says is wrong in this cell. An empty array clears the
   *  squiggles; undefined means the kernel has never had an opinion. */
  diagnostics?: LspDiagnostic[];
}

/** Who owns the markers we set, so clearing ours never clears Monaco's own. */
const MARKER_OWNER = 'clrkernel';

/** Tallest a single cell grows before it scrolls internally. */
const MAX_CELL_HEIGHT = 600;

/** Same idea for the production diff, which gets more room — it is the page. */
const MAX_DIFF_HEIGHT = 720;

/**
 * A Monaco editor for one notebook cell: it sizes itself to its content, swaps
 * language without losing undo history, and disposes cleanly.
 *
 * With a binding it shows the notebook's model for that cell rather than one of
 * its own, so the document survives the editor — that is what lets Focus Mode
 * show one cell at a time and still find the others as you left them. Without
 * one (the Source tab, the diff) it makes and disposes its own.
 */
export function useCellEditor(
  language: string,
  value: string,
  onChange: (value: string) => void,
  readOnly = false,
  binding?: CellBinding,
  /** Bump to push <c>value</c> back into the editor — see useFillEditor. */
  resetKey: unknown = 0,
) {
  const container = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const latestOnChange = useRef(onChange);
  latestOnChange.current = onChange;
  // Read through a ref, so a language change or a cell moving is picked up by the
  // next request rather than needing the model re-registered.
  const latestBinding = useRef(binding);
  latestBinding.current = binding;

  useEffect(() => {
    if (!container.current) {
      return;
    }
    // A cell's model belongs to the notebook and outlives this editor, so that
    // switching cells in Focus Mode keeps undo history and cursor position. The
    // Source tab and the diff are not cells: they get a model of their own, made
    // and disposed here as before.
    const binding = latestBinding.current;
    const shared = binding == null ? null : getCellModel(binding.cellId, language, value);
    const created = monaco.editor.create(container.current, {
      ...cellEditorOptions,
      ...(shared == null ? { value, language } : { model: shared }),
      readOnly,
    });
    editor.current = created;

    // Claim this model as a cell. Monaco's providers are global per language, so
    // this is what separates a cell from the Source tab and the diff panes.
    const model = created.getModel();
    if (model != null && binding != null) {
      bindCell(model, {
        path: binding.path,
        cellId: binding.cellId,
        languageId: () => latestBinding.current?.languageId ?? 'csharp-script',
        enabled: () => latestBinding.current?.enabled ?? true,
      });
    }

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
      // The editor first, then the model. The other way round leaves a live
      // editor attached to a disposed model for one turn, and Monaco's own
      // observables read that model's version while they unsubscribe —
      // "Model is disposed!" thrown from inside Monaco, with nothing of ours
      // in the stack.
      const model = created.getModel();
      created.dispose();
      // A cell's model is the notebook's, not this editor's: unmounting a cell
      // editor (switching modes, scrolling a cell out of the tree) must not take
      // the document with it. releaseCellModels disposes them when the cell is
      // actually gone. Anything else made its own model here and owns it.
      if (shared == null) {
        model?.dispose();
      }
      editor.current = null;
    };
    // Created once per cell: value and language are applied below so typing
    // never rebuilds the editor (which would drop the cursor and undo stack).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Squiggles. The kernel pushes these after a document changes, and an empty
  // list is a real answer — it is how a fixed error stops being drawn — so this
  // runs on [] as readily as on a list of problems.
  useEffect(() => {
    const model = editor.current?.getModel();
    if (model == null || binding?.diagnostics == null) {
      return;
    }
    monaco.editor.setModelMarkers(
      model,
      MARKER_OWNER,
      binding.diagnostics.map((d) => toMonacoMarker(d, monaco.MarkerSeverity as never)),
    );
  }, [binding?.diagnostics]);

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
  const latestValue = useRef(value);
  latestValue.current = value;

  useEffect(() => {
    const current = editor.current;
    if (current && current.getValue() !== latestValue.current) {
      current.setValue(latestValue.current);
    }
    // Deliberately not [value] — see resetKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  return container;
}

/**
 * One editor filling its container, scrolling inside itself — the Source tab and
 * anything else that is a whole file rather than a cell.
 *
 * Separate from `useCellEditor` because the two size in opposite directions: a
 * cell grows to its content and never scrolls, this takes whatever height the
 * pane gives it. It owns its model outright; nothing else refers to it.
 */
export function useFillEditor(
  language: string,
  value: string,
  onChange: (value: string) => void,
  readOnly = false,
  /**
   * Bump to push <paramref name="value"/> back into the editor.
   *
   * Nothing else does. `value` is React state fed by this editor's own
   * `onChange`, so it arrives a render behind what has been typed — syncing on
   * every change means that during a fast burst the editor is repeatedly reset to
   * a prefix of the sentence, with the cursor sent back with it. Characters go
   * missing, and with autosave running they go missing from the file too.
   *
   * The cases that genuinely need the buffer replaced — a merge landing under
   * you, a reload — say so by changing this.
   */
  resetKey: unknown = 0,
  /**
   * The editor's primary action, on Ctrl/Cmd+Enter and F5.
   *
   * It is handed the selected text when there is a selection and the whole buffer
   * otherwise. That rule lives here rather than at the call site because it is the
   * one everybody already has in their fingers from SSMS — run what is highlighted
   * — and a second copy of it is a second chance to get it wrong.
   */
  onRun?: (text: string) => void,
) {
  const container = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const latestOnChange = useRef(onChange);
  latestOnChange.current = onChange;
  const latestOnRun = useRef(onRun);
  latestOnRun.current = onRun;

  useEffect(() => {
    if (!container.current) {
      return;
    }
    const created = monaco.editor.create(container.current, {
      ...focusEditorOptions,
      value,
      language,
      readOnly,
    });
    editor.current = created;
    const changeListener = created.onDidChangeModelContent(() =>
      latestOnChange.current(created.getValue()),
    );
    function run() {
      const selection = created.getSelection();
      const selected = selection == null ? '' : (created.getModel()?.getValueInRange(selection) ?? '');
      latestOnRun.current?.(selected.trim().length > 0 ? selected : created.getValue());
    }
    // Both, because both are already in people's fingers: Ctrl+Enter from the
    // notebook, F5 from every query tool there has ever been. addCommand rather
    // than addAction — an action would also add a context-menu entry for
    // something the toolbar is already showing.
    created.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, run);
    created.addCommand(monaco.KeyCode.F5, run);
    return () => {
      changeListener.dispose();
      // The editor first, then the model. The other way round leaves a live
      // editor attached to a disposed model for one turn, and Monaco's own
      // observables read that model's version while they unsubscribe —
      // "Model is disposed!" thrown from inside Monaco, with nothing of ours
      // in the stack.
      const model = created.getModel();
      created.dispose();
      model?.dispose();
      editor.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const model = editor.current?.getModel();
    if (model) {
      monaco.editor.setModelLanguage(model, language);
    }
  }, [language]);

  const latestValue = useRef(value);
  latestValue.current = value;

  useEffect(() => {
    const current = editor.current;
    if (current && current.getValue() !== latestValue.current) {
      current.setValue(latestValue.current);
    }
    // Deliberately not [value] — see resetKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  return container;
}

/** Focus Mode's editor fills its pane and scrolls itself, unlike a cell editor
 *  which grows to its content and never scrolls. */
export const focusEditorOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
  ...cellEditorOptions,
  lineNumbers: 'on',
  lineNumbersMinChars: 3,
  lineDecorationsWidth: 10,
  scrollBeyondLastLine: false,
  // The pane is the scroll container; Monaco owns the scrollbar inside it, which
  // is why the pane itself must not be overflow:auto.
  scrollbar: { alwaysConsumeMouseWheel: true, vertical: 'auto', horizontal: 'auto' },
};

export interface FocusEditorOptions {
  /** Viewers read the notebook in the same layout; they just cannot type in it. */
  readOnly?: boolean;
  binding: CellBinding | null;
  language: string;
  /** The active cell's text, for seeding a model the notebook has not made yet. */
  value: string;
  onChange: (value: string) => void;
  onRun: () => void;
  onRunAndAdvance: () => void;
  onStep: (delta: number) => void;
}

/**
 * The single editor behind Focus Mode.
 *
 * One instance for the whole notebook, with `setModel` on every cell change —
 * creating and disposing an editor per switch is slow and throws away undo
 * history. Cursor, selection, scroll and folds ride along in a view state saved
 * before each switch, so a cell comes back exactly as you left it.
 */
export function useFocusEditor({
  readOnly = false, binding, language, value, onChange, onRun, onRunAndAdvance, onStep,
}: FocusEditorOptions) {
  const container = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const viewStates = useRef(new Map<string, monaco.editor.ICodeEditorViewState | null>());
  const showing = useRef<string | null>(null);

  // Commands are bound once, at creation, so everything they touch is read
  // through a ref — a handler that closed over the first render's props would
  // run the first cell forever.
  const latest = useRef({ binding, language, value, onChange, onRun, onRunAndAdvance, onStep });
  latest.current = { binding, language, value, onChange, onRun, onRunAndAdvance, onStep };

  useEffect(() => {
    if (!container.current) {
      return;
    }
    const created = monaco.editor.create(container.current, { ...focusEditorOptions, readOnly });
    editor.current = created;

    const changeListener = created.onDidChangeModelContent(() =>
      latest.current.onChange(created.getValue()),
    );

    created.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => latest.current.onRun());
    created.addCommand(monaco.KeyMod.Shift | monaco.KeyCode.Enter, () =>
      latest.current.onRunAndAdvance());
    created.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.UpArrow, () => latest.current.onStep(-1));
    created.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.DownArrow, () => latest.current.onStep(1));

    return () => {
      // Only the editor: every model here belongs to the notebook.
      changeListener.dispose();
      created.dispose();
      editor.current = null;
      showing.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Swap the model when the active cell changes, saving where you were first.
  useEffect(() => {
    const current = editor.current;
    if (current == null) {
      return;
    }
    if (binding == null) {
      current.setModel(null);
      showing.current = null;
      return;
    }
    if (showing.current === binding.cellId) {
      return;
    }
    if (showing.current != null) {
      viewStates.current.set(showing.current, current.saveViewState());
    }
    const model = getCellModel(binding.cellId, language, value);
    current.setModel(model);
    monaco.editor.setModelLanguage(model, language);
    // Re-claim the model for the language providers with THIS component's
    // getters: the binding Normal Mode registered closes over a component that
    // is no longer mounted, and would answer with whatever it last saw.
    bindCell(model, {
      path: binding.path,
      cellId: binding.cellId,
      languageId: () => latest.current.binding?.languageId ?? 'csharp-script',
      enabled: () => latest.current.binding?.enabled ?? true,
    });
    const saved = viewStates.current.get(binding.cellId);
    if (saved != null) {
      current.restoreViewState(saved);
    }
    showing.current = binding.cellId;
    current.focus();
  }, [binding, language, value]);

  // The picker can change the language without the cell changing.
  useEffect(() => {
    const model = editor.current?.getModel();
    if (model != null) {
      monaco.editor.setModelLanguage(model, language);
    }
  }, [language]);

  useEffect(() => {
    const model = editor.current?.getModel();
    if (model == null || binding?.diagnostics == null) {
      return;
    }
    monaco.editor.setModelMarkers(
      model,
      MARKER_OWNER,
      binding.diagnostics.map((d) => toMonacoMarker(d, monaco.MarkerSeverity as never)),
    );
  }, [binding?.diagnostics, binding?.cellId]);

  /** Re-measures after a splitter drag or a sidebar resize. */
  const relayout = useCallback(() => editor.current?.layout(), []);

  return { container, relayout };
}

/**
 * A read-only side-by-side diff. Used for "Diff vs production", where the two
 * sides are the same notebook on the two branches.
 */
export function useDiffEditor(original: string, modified: string, language: string, fill = false) {
  const container = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!container.current) {
      return;
    }
    // A diff editor never sizes itself, and a fixed height leaves a short file
    // sitting in a mostly empty box. Count the lines instead, with a floor so a
    // one-line change still has room for the header and a cap so a long file
    // scrolls rather than pushing the page away.
    const lines = Math.max(original.split('\n').length, modified.split('\n').length);
    const wanted = Math.max(lines, 8) * 19 + 24;
    if (fill) {
      // The pane decides: the tab is full height, so the diff is too.
      container.current.style.height = '100%';
    } else {
      container.current.style.height = `${Math.min(wanted, MAX_DIFF_HEIGHT)}px`;
    }
    // Whether the ruler earns its column: it is for finding changes you cannot
    // see, so it depends on the diff overflowing the box it ended up in.
    const overflows = wanted > (fill ? container.current.clientHeight : MAX_DIFF_HEIGHT);

    const editor = monaco.editor.createDiffEditor(container.current, {
      automaticLayout: true,
      readOnly: true,
      // Two panes need width; below that the inline view stays readable.
      renderSideBySide: container.current.clientWidth > 900,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      scrollbar: { vertical: 'auto', horizontal: 'auto' },
      // Both are needed: renderOverviewRuler drops the diff's own ruler, and
      // overviewRulerLanes drops the one each inner editor draws for itself.
      renderOverviewRuler: overflows,
      overviewRulerLanes: overflows ? 3 : 0,
      fontSize: 13,
    });
    const originalModel = monaco.editor.createModel(original, language);
    const modifiedModel = monaco.editor.createModel(modified, language);
    editor.setModel({ original: originalModel, modified: modifiedModel });

    return () => {
      // The editor first, then the model. The other way round leaves a live
      // editor attached to a disposed model for one turn, and Monaco's own
      // observables read that model's version while they unsubscribe —
      // "Model is disposed!" thrown from inside Monaco, with nothing of ours
      // in the stack.
      editor.dispose();
      originalModel.dispose();
      modifiedModel.dispose();
    };
  }, [original, modified, language]);

  return container;
}
