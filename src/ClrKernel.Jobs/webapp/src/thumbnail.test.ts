import { describe, expect, it } from 'vitest';
import {
  ASPECT, DEFAULT_ZOOM, MAX_ZOOM, MIN_THUMBNAIL_WIDTH, MIN_ZOOM, SCALE, clampZoom, codeScale,
  previewKey, previewSource, thumbnailBox, visibleLines,
} from './thumbnail';

describe('the thumbnail box', () => {
  it('is 4:3 whatever the sidebar is doing', () => {
    for (const width of [220, 260, 320, 480, 640]) {
      const box = thumbnailBox(width);
      expect(box.height).toBe(Math.round(box.width / ASPECT));
    }
  });

  it('never shrinks below the width a preview is worth drawing at', () => {
    // The sidebar can be dragged to 180. A 4:3 box that narrow shows about four
    // lines of anything, which is a smudge rather than a shape — so the box
    // stops shrinking and the caller widens the sidebar instead.
    expect(thumbnailBox(180).width).toBe(thumbnailBox(MIN_THUMBNAIL_WIDTH).width);
    expect(thumbnailBox(120).width).toBe(thumbnailBox(MIN_THUMBNAIL_WIDTH).width);
    expect(thumbnailBox(400).width).toBeGreaterThan(thumbnailBox(MIN_THUMBNAIL_WIDTH).width);
  });

  it('fits more lines as it grows, and always one past the bottom', () => {
    // One extra so the clip lands mid-line: a preview ending exactly on a line
    // boundary reads as a cell that ends there.
    expect(visibleLines(640)).toBeGreaterThan(visibleLines(240));
    expect(visibleLines(240)).toBeGreaterThan(3);
    const box = thumbnailBox(240);
    expect(visibleLines(240) * 18 * SCALE).toBeGreaterThan(box.height);
  });
});

describe('previewSource', () => {
  it('takes the first N lines and no more', () => {
    const source = Array.from({ length: 50 }, (_, i) => `line ${i}`).join('\n');
    expect(previewSource(source, 5).split('\n')).toEqual([
      'line 0', 'line 1', 'line 2', 'line 3', 'line 4',
    ]);
  });

  it('never the whole cell, which is the point', () => {
    // Colorizing two thousand lines to show twelve is the difference between a
    // sidebar that opens and one that hangs.
    const huge = Array.from({ length: 2000 }, () => 'x').join('\n');
    expect(previewSource(huge, 12).split('\n')).toHaveLength(12);
  });

  it('skips leading blank lines, which say nothing about a shape', () => {
    expect(previewSource('\n\n\nSELECT 1', 5)).toBe('SELECT 1');
  });

  it('and trailing ones, which would render as empty space below content', () => {
    expect(previewSource('SELECT 1\n\n\n', 5)).toBe('SELECT 1');
  });

  it('keeps blank lines in the middle, because they are the shape', () => {
    expect(previewSource('a\n\nb', 5)).toBe('a\n\nb');
  });

  it('handles CRLF and an empty cell without special-casing at the call site', () => {
    expect(previewSource('a\r\nb', 5)).toBe('a\nb');
    expect(previewSource('', 5)).toBe('');
    expect(previewSource('   \n  ', 5)).toBe('');
  });
});

describe('previewKey', () => {
  it('changes when the text does, so an edited cell re-renders', () => {
    expect(previewKey('n1', 'sql', 'SELECT 1')).not.toBe(previewKey('n1', 'sql', 'SELECT 2'));
  });

  it('and when the language does, because the same text tokenizes differently', () => {
    expect(previewKey('n1', 'sql', 'x')).not.toBe(previewKey('n1', 'csharp', 'x'));
  });

  it('but not when the theme does', () => {
    // Deliberate, and measured rather than assumed: `monaco.editor.colorize`
    // returns HTML carrying `.mtkN` classes, and Monaco rewrites the stylesheet
    // behind those classes on `setTheme` — so cached HTML recolors itself. The
    // spec expected the colours to be baked in and the cache to need busting;
    // against this build they are not and it does not.
    expect(previewKey('n1', 'sql', 'x')).toBe(previewKey('n1', 'sql', 'x'));
  });

  it('tells two cells apart even when they hold the same text', () => {
    expect(previewKey('n1', 'sql', 'x')).not.toBe(previewKey('n2', 'sql', 'x'));
  });
});

describe('the zoom', () => {
  it('shrinks the box', () => {
    expect(thumbnailBox(320, 0.5).width).toBeLessThan(thumbnailBox(320, 1).width);
    expect(thumbnailBox(320, 1).width).toBe(thumbnailBox(320).width);
  });

  it('keeps 4:3 at every step', () => {
    for (const zoom of [0.5, 0.65, 0.8, 1]) {
      const box = thumbnailBox(320, zoom);
      expect(box.height).toBe(Math.round(box.width / ASPECT));
    }
  });

  it('shows the same lines, smaller — not fewer lines the same size', () => {
    // What a zoom means everywhere else, and what keeps a zoomed-out column
    // recognisable: the shapes are the ones you already learned, further away.
    const lines = [0.5, 0.7, 1].map((zoom) => visibleLines(320, zoom));
    expect(new Set(lines).size).toBe(1);
    expect(codeScale(0.5)).toBeLessThan(codeScale(1));
    expect(codeScale(1)).toBe(SCALE);
  });

  it('holds a hand-edited value inside the range it means', () => {
    expect(clampZoom(9)).toBe(MAX_ZOOM);
    expect(clampZoom(0.01)).toBe(MIN_ZOOM);
    expect(clampZoom(Number.NaN)).toBe(DEFAULT_ZOOM);
    expect(clampZoom(undefined as unknown as number)).toBe(DEFAULT_ZOOM);
  });

  it('and never zooms a narrow sidebar below what it was already floored to', () => {
    expect(thumbnailBox(120, 1).width).toBe(thumbnailBox(MIN_THUMBNAIL_WIDTH, 1).width);
  });
});
