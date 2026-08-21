/**
 * How much of the notebook toolbar fits.
 *
 * The toolbar must never wrap to a second row — a notebook page that spends two
 * rows on chrome is what this redesign is undoing. So instead of letting flex
 * wrap, it sheds detail in a fixed order as the window narrows.
 *
 * React-free and measured in viewport pixels, because the rail is a fixed 48px
 * and the content region is everything else: the mapping is constant, and these
 * are the numbers the spec is written in.
 */

export interface ToolbarLayout {
  /** Execution controls move behind a single overflow button. */
  collapse: boolean;
  runAllIconOnly: boolean;
  restartIconOnly: boolean;
  /** The kernel badge keeps its status word and dot but drops the version. */
  showKernelVersion: boolean;
}

/** Widths at which something has to give. */
export const BREAKPOINTS = { collapse: 1024, tight: 1200, compact: 1400 } as const;

export function toolbarLayout(width: number): ToolbarLayout {
  if (width < BREAKPOINTS.collapse) {
    // The icon-only flags stay set even though the collapsed menu renders
    // labels: the layout only ever sheds detail as the window narrows, and a
    // flag that flips back on at the narrowest size is a trap for the next
    // reader.
    return {
      collapse: true,
      runAllIconOnly: true,
      restartIconOnly: true,
      showKernelVersion: false,
    };
  }
  if (width < BREAKPOINTS.tight) {
    return {
      collapse: false,
      runAllIconOnly: true,
      restartIconOnly: true,
      showKernelVersion: false,
    };
  }
  if (width < BREAKPOINTS.compact) {
    return {
      collapse: false,
      runAllIconOnly: false,
      // Restart is the less-used of the two, so it sheds its label first.
      restartIconOnly: true,
      showKernelVersion: true,
    };
  }
  return {
    collapse: false,
    runAllIconOnly: false,
    restartIconOnly: false,
    showKernelVersion: true,
  };
}

/**
 * What the kernel badge says. `null` when execution is unavailable — the badge
 * is then not rendered at all rather than reporting a kernel that cannot run.
 */
export function kernelLabel(
  session: { started?: boolean; kernel?: string | null; version?: string | null } | null,
  running: boolean,
  showVersion: boolean,
): { text: string; state: 'running' | 'idle' | 'stopped' } {
  if (running) {
    return { text: 'running', state: 'running' };
  }
  if (!session?.started) {
    return { text: 'no kernel', state: 'stopped' };
  }
  const name = session.kernel ?? 'kernel';
  return {
    text: showVersion && session.version ? `${name} ${session.version} · idle` : 'idle',
    state: 'idle',
  };
}

/**
 * Execution controls belong to the Notebook tab. On Source and Diff they are
 * hidden rather than disabled — a greyed-out Run All on a diff view is noise,
 * not information. Saving and promoting are document-level and stay everywhere.
 */
export function showsExecution(tab: string): boolean {
  return tab === 'notebook';
}
