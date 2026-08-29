import { describe, expect, it } from 'vitest';
import {
  duration, joinSource, isInjectedParameters, parseAnsi, renderOutput, timeAgo, timeUntil,
} from './ipynb';

describe('joinSource', () => {
  it('accepts both nbformat source shapes', () => {
    expect(joinSource('a line')).toBe('a line');
    expect(joinSource(['one\n', 'two'])).toBe('one\ntwo');
    expect(joinSource(undefined)).toBe('');
  });
});

describe('renderOutput', () => {
  it('prefers html over plain text', () => {
    const rendered = renderOutput({
      output_type: 'execute_result',
      data: { 'text/html': '<table><tr><td>1</td></tr></table>', 'text/plain': '1' },
    });
    expect(rendered).toEqual({ kind: 'html', html: '<table><tr><td>1</td></tr></table>' });
  });

  it('falls back to plain text', () => {
    expect(renderOutput({ output_type: 'display_data', data: { 'text/plain': '42' } })).toEqual({
      kind: 'text',
      text: '42',
    });
  });

  it('renders stream output', () => {
    expect(renderOutput({ output_type: 'stream', name: 'stdout', text: ['a\n', 'b'] })).toEqual({
      kind: 'text',
      text: 'a\nb',
    });
  });

  it('renders errors with their traceback', () => {
    expect(
      renderOutput({
        output_type: 'error',
        ename: 'InvalidOperationException',
        evalue: 'boom',
        traceback: ['at A', 'at B'],
      }),
    ).toEqual({
      kind: 'error',
      ename: 'InvalidOperationException',
      evalue: 'boom',
      traceback: 'at A\nat B',
    });
  });

  it('names an unrenderable mime type instead of dropping it silently', () => {
    expect(renderOutput({ output_type: 'display_data', data: { 'image/png': 'base64…' } })).toEqual({
      kind: 'text',
      text: '[image/png]',
    });
    expect(renderOutput({ output_type: 'display_data' })).toBeNull();
  });
});

describe('parseAnsi', () => {
  it('returns one plain span when there are no codes', () => {
    expect(parseAnsi('plain text')).toEqual([{ text: 'plain text', className: undefined }]);
  });

  it('maps colour codes to classes and resets them', () => {
    expect(parseAnsi('\u001b[31mred\u001b[0m normal')).toEqual([
      { text: 'red', className: 'ansi-red' },
      { text: ' normal', className: undefined },
    ]);
  });

  it('combines bold with a colour', () => {
    expect(parseAnsi('\u001b[1;32mok\u001b[0m')).toEqual([
      { text: 'ok', className: 'ansi-bold ansi-green' },
    ]);
  });

  it('drops codes it does not understand rather than printing them', () => {
    const spans = parseAnsi('\u001b[48;5;200mstyled\u001b[0m');
    expect(spans.map((s) => s.text).join('')).toBe('styled');
  });
});

describe('isInjectedParameters', () => {
  it('detects the tag the runner writes', () => {
    expect(isInjectedParameters({ cell_type: 'code', source: '', metadata: { tags: ['injected-parameters'] } })).toBe(
      true,
    );
    expect(isInjectedParameters({ cell_type: 'code', source: '' })).toBe(false);
  });
});

describe('timeAgo and duration', () => {
  const now = Date.parse('2026-01-01T12:00:00Z');

  it('treats server timestamps without a zone as UTC', () => {
    expect(timeAgo('2026-01-01T11:59:30', now)).toBe('30s ago');
    expect(timeAgo('2026-01-01T11:59:30Z', now)).toBe('30s ago');
  });

  it('scales the unit with the age', () => {
    expect(timeAgo('2026-01-01T11:30:00Z', now)).toBe('30m ago');
    expect(timeAgo('2026-01-01T09:00:00Z', now)).toBe('3h ago');
    expect(timeAgo('2025-12-30T12:00:00Z', now)).toBe('2d ago');
    expect(timeAgo(null, now)).toBe('—');
  });

  // timeAgo clamps at zero, so a scheduled run three hours out would read
  // "0s ago" — not merely imprecise, the opposite of true.
  it('counts forwards for something that has not happened yet', () => {
    expect(timeUntil('2026-01-01T12:00:30Z', now)).toBe('in 30s');
    expect(timeUntil('2026-01-01T14:00:00Z', now)).toBe('in 2h');
    expect(timeUntil('2026-01-03T12:00:00Z', now)).toBe('in 2d');
    expect(timeAgo('2026-01-01T15:00:00Z', now)).toBe('0s ago');
  });

  it('says due rather than counting backwards past the moment', () => {
    expect(timeUntil('2026-01-01T11:59:55Z', now)).toBe('due');
    expect(timeUntil(null, now)).toBe('—');
  });

  it('formats elapsed time', () => {
    expect(duration('2026-01-01T12:00:00Z', '2026-01-01T12:00:00.250Z')).toBe('250ms');
    expect(duration('2026-01-01T12:00:00Z', '2026-01-01T12:00:01.500Z')).toBe('1.5s');
    expect(duration('2026-01-01T12:00:00Z', '2026-01-01T12:02:30Z')).toBe('2m 30s');
    expect(duration('2026-01-01T12:00:00Z', null)).toBe('—');
  });
});
