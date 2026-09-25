import { describe, expect, it } from 'vitest';
import type { RunCell } from './api';
import { pairCells } from './runCells';

const cell = (cellIndex: number): RunCell =>
  ({ cellIndex, status: 'Succeeded', sourcePreview: `line ${cellIndex}` }) as RunCell;

describe('pairCells', () => {
  it('skips markdown and matches code cells by order, injected cell included', () => {
    const paired = pairCells([cell(0), cell(1)], {
      cells: [
        { cell_type: 'markdown', source: '# Title' },
        { cell_type: 'code', source: ['var x = 1;'], metadata: { tags: ['injected-parameters'] } },
        { cell_type: 'markdown', source: 'prose' },
        { cell_type: 'code', source: 'x + 1', outputs: [{ output_type: 'stream', text: '2' }] },
      ],
    });
    expect(paired[0].source).toBe('var x = 1;');
    expect(paired[0].outputs).toEqual([]);
    expect(paired[1].source).toBe('x + 1');
    expect(paired[1].outputs).toHaveLength(1);
  });

  it('has no source or outputs while the artifact is absent', () => {
    const [only] = pairCells([cell(0)], null);
    expect(only.source).toBeNull();
    expect(only.outputs).toEqual([]);
  });
});
