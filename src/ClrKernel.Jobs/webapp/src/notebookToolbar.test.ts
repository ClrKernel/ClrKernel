import { describe, expect, it } from 'vitest';
import { BREAKPOINTS, kernelLabel, showsExecution, toolbarLayout } from './notebookToolbar';

describe('toolbarLayout', () => {
  it('shows everything on a wide window', () => {
    expect(toolbarLayout(1600)).toEqual({
      collapse: false,
      runAllIconOnly: false,
      restartIconOnly: false,
      showKernelVersion: true,
    });
  });

  it('drops Restart’s label first', () => {
    const layout = toolbarLayout(1300);
    expect(layout.restartIconOnly).toBe(true);
    expect(layout.runAllIconOnly).toBe(false);
    expect(layout.collapse).toBe(false);
  });

  it('then drops Run All’s label and the kernel version', () => {
    const layout = toolbarLayout(1100);
    expect(layout.runAllIconOnly).toBe(true);
    expect(layout.showKernelVersion).toBe(false);
    expect(layout.collapse).toBe(false);
  });

  it('collapses execution controls below 1024', () => {
    expect(toolbarLayout(1000).collapse).toBe(true);
  });

  it('degrades monotonically — nothing comes back as the window narrows', () => {
    const widths = [1600, 1400, 1399, 1200, 1199, 1024, 1023, 800];
    const layouts = widths.map(toolbarLayout);
    const score = (l: ReturnType<typeof toolbarLayout>) =>
      Number(l.restartIconOnly) + Number(l.runAllIconOnly) + Number(!l.showKernelVersion) +
      Number(l.collapse);
    for (let i = 1; i < layouts.length; i++) {
      expect(score(layouts[i]), `at ${widths[i]}px`).toBeGreaterThanOrEqual(score(layouts[i - 1]));
    }
  });

  it('is exact at each documented breakpoint', () => {
    expect(toolbarLayout(BREAKPOINTS.compact).restartIconOnly).toBe(false);
    expect(toolbarLayout(BREAKPOINTS.compact - 1).restartIconOnly).toBe(true);
    expect(toolbarLayout(BREAKPOINTS.tight).runAllIconOnly).toBe(false);
    expect(toolbarLayout(BREAKPOINTS.tight - 1).runAllIconOnly).toBe(true);
    expect(toolbarLayout(BREAKPOINTS.collapse).collapse).toBe(false);
    expect(toolbarLayout(BREAKPOINTS.collapse - 1).collapse).toBe(true);
  });
});

describe('kernelLabel', () => {
  it('reports a run in flight before anything else', () => {
    expect(kernelLabel({ started: true, kernel: 'ClrKernel', version: '0.10' }, true, true)).toEqual({
      text: 'running',
      state: 'running',
    });
  });

  it('says so when no kernel has started', () => {
    expect(kernelLabel({ started: false }, false, true).state).toBe('stopped');
    expect(kernelLabel(null, false, true).state).toBe('stopped');
  });

  it('carries the version when there is room', () => {
    expect(kernelLabel({ started: true, kernel: 'ClrKernel', version: '0.10.0' }, false, true).text)
      .toBe('ClrKernel 0.10.0 · idle');
  });

  it('keeps the status word when there is not', () => {
    expect(kernelLabel({ started: true, kernel: 'ClrKernel', version: '0.10.0' }, false, false).text)
      .toBe('idle');
  });

  it('does not print an empty version', () => {
    expect(kernelLabel({ started: true, kernel: 'ClrKernel', version: null }, false, true).text)
      .toBe('idle');
  });
});

describe('showsExecution', () => {
  it('is the Notebook tab only', () => {
    expect(showsExecution('notebook')).toBe(true);
    expect(showsExecution('source')).toBe(false);
    expect(showsExecution('diff')).toBe(false);
  });
});
