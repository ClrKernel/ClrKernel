import type { RunCell } from './api';
import { joinSource, type Notebook, type NotebookOutput } from './ipynb';

/** A run cell with what the artifact recorded for it, once there is one. */
export interface PairedCell {
  cell: RunCell;
  /** The full source, or null while the run is live and the artifact not yet written. */
  source: string | null;
  outputs: NotebookOutput[];
}

/**
 * Run cells beside their artifact cells, matched by order.
 *
 * `RunCell.cellIndex` is the code-cell index of the executed plan, which counts
 * the injected parameters cell, and the artifact is that same plan written out —
 * so the artifact's code cells, in order, are the run cells, in order.
 */
export function pairCells(runCells: RunCell[], notebook: Notebook | null): PairedCell[] {
  const code = (notebook?.cells ?? []).filter((c) => c.cell_type === 'code');
  return runCells.map((cell) => {
    const artifact = code[cell.cellIndex];
    return {
      cell,
      source: artifact ? joinSource(artifact.source) : null,
      outputs: artifact?.outputs ?? [],
    };
  });
}
