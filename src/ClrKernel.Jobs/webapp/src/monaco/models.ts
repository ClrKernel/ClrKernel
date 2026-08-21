import { unbindCell } from './language';
import { monaco } from './setup';

/**
 * One Monaco text model per notebook cell, owned by the notebook rather than by
 * whichever editor happens to be showing it.
 *
 * Editors used to mint their own model and dispose it on unmount, which is fine
 * while every cell has an editor on screen. Focus Mode shows one cell at a time,
 * so a cell's model has to outlive its editor: a model that is disposed and
 * recreated loses undo history and cursor position, and "return to a cell and
 * find it as you left it" is the whole point of switching cells.
 *
 * Keyed by cell id, not by URI. Monaco throws if two models claim one URI, and
 * React's double-mount arranges exactly that.
 */
const models = new Map<string, monaco.editor.ITextModel>();

/**
 * The model for a cell, created on first use. `initial` seeds a new model only —
 * an existing one is left alone, because it is the live document and the caller's
 * copy may be a render behind.
 */
export function getCellModel(
  cellId: string,
  language: string,
  initial: string,
): monaco.editor.ITextModel {
  const existing = models.get(cellId);
  if (existing != null && !existing.isDisposed()) {
    return existing;
  }
  const model = monaco.editor.createModel(initial, language);
  models.set(cellId, model);
  return model;
}

/**
 * Disposes the models of cells that no longer exist — a deleted cell, or the
 * whole notebook when you navigate away. Keeping them would leak a model per
 * cell per notebook opened, and would leave deleted cells reachable as Go to
 * Definition targets.
 */
export function releaseCellModels(keep: Iterable<string>): void {
  const keeping = new Set(keep);
  for (const [cellId, model] of [...models]) {
    if (keeping.has(cellId)) {
      continue;
    }
    models.delete(cellId);
    unbindCell(cellId);
    if (!model.isDisposed()) {
      model.dispose();
    }
  }
}
