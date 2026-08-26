import { describe, expect, it } from 'vitest';
import { withIds, type EditorCell } from './notebook';
import {
  buildToc,
  headingLevel,
  leafLabel,
  neighbourCell,
  sectionIds,
  stepCell,
  visibleLeaves,
  type TocSection,
} from './toc';

const code = (source: string): Omit<EditorCell, 'id'> => ({
  kind: 'code', tag: 'csharp', languageId: null, source,
} as Omit<EditorCell, 'id'>);

const md = (source: string): Omit<EditorCell, 'id'> => ({
  kind: 'markdown', tag: null, languageId: null, source,
} as Omit<EditorCell, 'id'>);

const build = (...cells: Omit<EditorCell, 'id'>[]) => buildToc(withIds(cells as EditorCell[]));

const shape = (nodes: ReturnType<typeof buildToc>): unknown =>
  nodes.map((n) => (n.kind === 'leaf' ? n.label : { [n.label]: shape(n.children) }));

describe('headingLevel', () => {
  it('reads a heading only from a markdown cell', () => {
    expect(headingLevel(withIds([md('# Setup')] as EditorCell[])[0])).toBe(1);
    expect(headingLevel(withIds([md('### Deep')] as EditorCell[])[0])).toBe(3);
    // A C# preprocessor line, or a shell comment, is not a section.
    expect(headingLevel(withIds([code('#r "nuget: Foo"')] as EditorCell[])[0])).toBe(0);
    expect(headingLevel(withIds([md('Just prose')] as EditorCell[])[0])).toBe(0);
    // "#hashtag" is not a heading — markdown needs the space.
    expect(headingLevel(withIds([md('#nospace')] as EditorCell[])[0])).toBe(0);
  });
});

describe('buildToc', () => {
  it('nests cells under the heading above them', () => {
    expect(shape(build(
      md('# Setup'), code('using System;'), code('var conn = 1;'),
      md('# Extract'), code('var df = 2;'),
    ))).toEqual([
      // The heading cell's own leaf reads "Setup", not "# Setup": the marker is
      // syntax, and the tree already shows the nesting it encodes.
      { Setup: ['Setup', 'using System;', 'var conn = 1;'] },
      { Extract: ['Extract', 'var df = 2;'] },
    ]);
  });

  it('keeps cells that appear before any heading', () => {
    // An implicit root group: these belong to the notebook, not to a section,
    // and dropping them would make cells unreachable from the tree.
    expect(shape(build(code('first'), md('# Later'), code('second'))))
      .toEqual(['first', { Later: ['Later', 'second'] }]);
  });

  it('nests by heading level and closes sections when the level rises', () => {
    expect(shape(build(
      md('# One'), code('a'), md('## Two'), code('b'), md('# Three'), code('c'),
    ))).toEqual([
      { One: ['One', 'a', { Two: ['Two', 'b'] }] },
      { Three: ['Three', 'c'] },
    ]);
  });

  it('flattens past H3 rather than growing indentation', () => {
    const toc = build(md('# One'), md('## Two'), md('### Three'), md('#### Four'), code('x'));
    const one = toc[0] as TocSection;
    const two = one.children.find((n) => n.kind === 'section') as TocSection;
    const three = two.children.find((n) => n.kind === 'section') as TocSection;
    const four = three.children.find((n) => n.kind === 'section') as TocSection;
    // H4 is a sibling of H3 in depth terms, so the tree stops indenting.
    expect([one.depth, two.depth, three.depth]).toEqual([1, 2, 3]);
    expect(four).toBeUndefined();
    expect(three.children.some((n) => n.kind === 'section' && n.label === 'Four')).toBe(false);
  });

  it('is empty for a notebook with no cells', () => {
    expect(buildToc([])).toEqual([]);
  });

  it('keeps the heading cell itself reachable as a leaf', () => {
    // Otherwise a markdown cell that happens to be a heading could never be
    // opened for editing from the tree.
    const toc = build(md('# Setup'));
    const section = toc[0] as TocSection;
    expect(section.children[0].kind).toBe('leaf');
  });
});

describe('leafLabel', () => {
  it('skips leading comments to show the line that does something', () => {
    const [cell] = withIds([code('// load the config\n// (slow)\nvar config = Load();')] as EditorCell[]);
    expect(leafLabel(cell).label).toBe('var config = Load();');
  });

  it('shows a directive, which is not a comment however much it looks like one', () => {
    const [cell] = withIds([code('#!sql-connect --name prod\nSELECT 1')] as EditorCell[]);
    expect(leafLabel(cell).label).toBe('#!sql-connect --name prod');
  });

  it('falls back to the comment when a cell is nothing else', () => {
    const [cell] = withIds([code('// TODO: write this')] as EditorCell[]);
    expect(leafLabel(cell).label).toBe('// TODO: write this');
  });

  it('truncates long lines but keeps the whole one for the tooltip', () => {
    const long = `var x = ${'a'.repeat(80)};`;
    const [cell] = withIds([code(long)] as EditorCell[]);
    const { label, title } = leafLabel(cell);
    expect(label.length).toBeLessThan(long.length);
    expect(label.endsWith('…')).toBe(true);
    expect(title).toBe(long);
  });

  it('says something rather than nothing for an empty cell', () => {
    const [cell] = withIds([code('   \n  ')] as EditorCell[]);
    expect(leafLabel(cell).label).toBe('(empty)');
  });
});

describe('visibleLeaves', () => {
  it('skips the contents of collapsed sections', () => {
    // Moving the selection into a section you cannot see is not moving it.
    const toc = build(md('# One'), code('a'), md('# Two'), code('b'));
    const ids = sectionIds(toc);
    expect(visibleLeaves(toc, new Set()).map((l) => l.label)).toEqual(['One', 'a', 'Two', 'b']);
    expect(visibleLeaves(toc, new Set([ids[0]])).map((l) => l.label)).toEqual(['Two', 'b']);
  });
});

describe('neighbourCell and stepCell', () => {
  it('falls to the next cell after a delete, or the previous when it was last', () => {
    const cells = withIds([code('a'), code('b'), code('c')] as EditorCell[]);
    const without1 = cells.filter((_, i) => i !== 1);
    expect(neighbourCell(without1, 1)).toBe(cells[2].id);
    const without2 = cells.filter((_, i) => i !== 2);
    expect(neighbourCell(without2, 2)).toBe(cells[1].id);
    expect(neighbourCell([], 0)).toBeNull();
  });

  it('steps through cells and stops at the ends rather than wrapping', () => {
    const cells = withIds([code('a'), code('b')] as EditorCell[]);
    expect(stepCell(cells, cells[0].id, 1)).toBe(cells[1].id);
    expect(stepCell(cells, cells[1].id, 1)).toBe(cells[1].id);
    expect(stepCell(cells, cells[0].id, -1)).toBe(cells[0].id);
    // An active cell that has been deleted falls back to the first.
    expect(stepCell(cells, 'gone', 1)).toBe(cells[0].id);
    expect(stepCell([], null, 1)).toBeNull();
  });
});
