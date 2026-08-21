import { useEffect, useRef } from 'react';
import { bindCell, unbindCell } from './language';
import { toMonacoMarker, type LspDiagnostic } from './lsp';
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
 * Disposing the *model* as well as the editor matters — Monaco models are global
 * and outlive the editor that showed them, so a notebook scrolled through a few
 * times would otherwise leak one per cell.
 */
export function useCellEditor(
  language: string,
  value: string,
  onChange: (value: string) => void,
  readOnly = false,
  binding?: CellBinding,
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
    const created = monaco.editor.create(container.current, {
      ...cellEditorOptions,
      value,
      language,
      readOnly,
    });
    editor.current = created;

    // Claim this model as a cell. Monaco's providers are global per language, so
    // this is what separates a cell from the Source tab and the diff panes.
    const model = created.getModel();
    if (model != null && latestBinding.current != null) {
      bindCell(model, {
        path: latestBinding.current.path,
        cellId: latestBinding.current.cellId,
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
      // Before the model goes: the definition registry is a strong map, so a cell
      // that is not unregistered both leaks its model and stays reachable as a
      // Go to Definition target after it has been deleted.
      if (latestBinding.current != null) {
        unbindCell(latestBinding.current.cellId);
      }
      created.getModel()?.dispose();
      created.dispose();
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
    // A diff editor never sizes itself, and a fixed height leaves a short file
    // sitting in a mostly empty box. Count the lines instead, with a floor so a
    // one-line change still has room for the header and a cap so a long file
    // scrolls rather than pushing the page away.
    const lines = Math.max(original.split('\n').length, modified.split('\n').length);
    const wanted = Math.max(lines, 8) * 19 + 24;
    container.current.style.height = `${Math.min(wanted, MAX_DIFF_HEIGHT)}px`;

    const editor = monaco.editor.createDiffEditor(container.current, {
      automaticLayout: true,
      readOnly: true,
      // Two panes need width; below that the inline view stays readable.
      renderSideBySide: container.current.clientWidth > 900,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      scrollbar: { vertical: 'auto', horizontal: 'auto' },
      // The ruler is for finding changes you cannot see. When the whole diff
      // fits on screen it is a grey column down the side and nothing else.
      // Both are needed: renderOverviewRuler drops the diff's own ruler, and
      // overviewRulerLanes drops the one each inner editor draws for itself.
      renderOverviewRuler: wanted > MAX_DIFF_HEIGHT,
      overviewRulerLanes: wanted > MAX_DIFF_HEIGHT ? 3 : 0,
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
