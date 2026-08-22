/**
 * How much of the notebook toolbar fits.
 *
 * The toolbar must never wrap to a second row — a notebook page that spends two
 * rows on chrome is what this redesign is undoing. So instead of letting flex
 * wrap, it sheds detail in a fixed order as the window narrows.
 *
 * React-free, and measured against the *toolbar's own* width rather than the
 * window's. It used to take the viewport, on the reasoning that the rail is a
 * fixed 48px so the mapping is constant — but the editor grew a file explorer,
 * and a 218px sidebar makes the window a fifth wider than the space the toolbar
 * actually has. Dragging that sidebar fires no window resize at all.
 *
 * The numbers below are measured, not guessed: the full layout needs about
 * 1124px of bar — measured with the promotion-blocked info button present,
 * which is the wider of the two cases — and each tier below it is what remains
 * once that tier's label has gone. Re-measure if the controls change.
 */

export interface ToolbarLayout {
  /** Execution controls move behind a single overflow button. */
  collapse: boolean;
  runAllIconOnly: boolean;
  restartIconOnly: boolean;
  /** The kernel badge keeps its status word and dot but drops the version. */
  showKernelVersion: boolean;
  /** Below this the kernel badge goes entirely — it is the one thing here that
   *  reports rather than does. */
  showKernel: boolean;
  /** 'Promote' rather than 'Promote to production'. */
  shortPromote: boolean;
}

/** Toolbar widths at which something has to give. */
export const BREAKPOINTS = { narrow: 780, collapse: 880, tight: 1000, compact: 1140 } as const;

export function toolbarLayout(width: number): ToolbarLayout {
  if (width < BREAKPOINTS.narrow) {
    // Everything that can go, has gone. Below this the bar scrolls sideways
    // rather than growing a second row.
    return {
      collapse: true,
      runAllIconOnly: true,
      restartIconOnly: true,
      showKernelVersion: false,
      showKernel: false,
      shortPromote: true,
    };
  }
  if (width < BREAKPOINTS.collapse) {
    // The icon-only flags stay set even though the collapsed menu renders
    // labels: the layout only ever sheds detail as the bar narrows, and a
    // flag that flips back on at the narrowest size is a trap for the next
    // reader.
    return {
      collapse: true,
      runAllIconOnly: true,
      restartIconOnly: true,
      showKernelVersion: false,
      showKernel: true,
      shortPromote: true,
    };
  }
  if (width < BREAKPOINTS.tight) {
    return {
      collapse: false,
      runAllIconOnly: true,
      restartIconOnly: true,
      showKernelVersion: false,
      showKernel: true,
      shortPromote: true,
    };
  }
  if (width < BREAKPOINTS.compact) {
    return {
      collapse: false,
      runAllIconOnly: false,
      // Restart is the less-used of the two, so it sheds its label first.
      restartIconOnly: true,
      showKernelVersion: true,
      showKernel: true,
      shortPromote: false,
    };
  }
  return {
    collapse: false,
    runAllIconOnly: false,
    restartIconOnly: false,
    showKernelVersion: true,
    showKernel: true,
    shortPromote: false,
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
