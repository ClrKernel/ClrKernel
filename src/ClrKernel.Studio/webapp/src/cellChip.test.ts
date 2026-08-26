import { describe, expect, it } from 'vitest';
import type { ApiCell, ApiLanguage } from './api';
import { CHIP_HUES, chipFor, hueFor } from './cellChip';
import { withIds } from './notebook';

const languages: ApiLanguage[] = [
  {
    id: 'sql', displayName: 'T-SQL', defaultSelector: '#!sql', selectors: ['#!sql'],
    languageTags: ['sql', 'tsql'], monogram: 'TSQL',
  },
  {
    id: 'oraclesql', displayName: 'Oracle SQL', defaultSelector: '#!oraclesql',
    selectors: ['#!oraclesql'], languageTags: ['oraclesql'], monogram: 'ORA',
  },
  {
    id: 'http', displayName: 'HTTP', defaultSelector: '#!http', selectors: ['#!http'],
    languageTags: ['http'], monogram: 'HTTP',
  },
  // A kernel that predates the field: no monogram at all.
  {
    id: 'toy', displayName: 'Toy', defaultSelector: '#!toy', selectors: ['#!toy'],
    languageTags: ['toy'],
  },
];

const cell = (over: Partial<ApiCell> = {}): ApiCell => ({
  kind: 'code', tag: 'csharp', languageId: null, source: '', ...over,
});

const chip = (over: Partial<ApiCell> = {}) => chipFor(withIds([cell(over)])[0], languages);

describe('the language chip', () => {
  it('takes its letters from the kernel', () => {
    expect(chip({ languageId: 'sql', tag: 'sql' }).label).toBe('TSQL');
    expect(chip({ languageId: 'http', tag: 'http' }).label).toBe('HTTP');
  });

  it('tells the SQL dialects apart, which is most of what the split was for', () => {
    expect(chip({ languageId: 'sql' }).label).not.toBe(chip({ languageId: 'oraclesql' }).label);
    expect(chip({ languageId: 'oraclesql' }).label).toBe('ORA');
  });

  it('names the two that are not registered languages at all', () => {
    // C# is the engine's own and Markdown is prose; neither has a descriptor to
    // ask, which is the same reason the language picker prepends them.
    expect(chip().label).toBe('C#');
    expect(chip({ kind: 'markdown', tag: null }).label).toBe('MD');
  });

  it('falls back to the id for a kernel too old to say', () => {
    expect(chip({ languageId: 'toy' }).label).toBe('TOY');
  });

  it('carries the full name for the tooltip and the accessible name', () => {
    expect(chip({ languageId: 'oraclesql' }).title).toBe('Oracle SQL');
    expect(chip({ kind: 'markdown', tag: null }).title).toBe('Markdown');
  });

  it('names a token and never a colour', () => {
    // The token layer is the only place a colour is written down, and a chip
    // that carried one would be the exception that ends that rule.
    for (const languageId of ['sql', 'oraclesql', 'http', 'toy', null]) {
      expect(chip({ languageId }).colorVar).toMatch(/^--lang-[1-6]$/);
    }
  });
});

describe('hueFor', () => {
  it('gives the same answer every time', () => {
    // It only has to be stable. Which hue a language lands on means nothing —
    // the letters identify the cell, the colour helps the eye group them.
    expect(hueFor('oraclesql')).toBe(hueFor('oraclesql'));
    expect(hueFor('sql')).toBe(hueFor('sql'));
  });

  it('stays inside the hues the token layer defines', () => {
    const ids = ['sql', 'oraclesql', 'ansisql', 'http', 'dax', 'mermaid', 'powershell',
      'shellscript', 'csharp', 'markdown', '', 'a-very-long-language-id-indeed'];
    for (const id of ids) {
      const hue = hueFor(id);
      expect(hue).toBeGreaterThanOrEqual(1);
      expect(hue).toBeLessThanOrEqual(CHIP_HUES);
    }
  });

  it('spreads the shipped languages over more than one hue', () => {
    // Not a guarantee it can make — six hues and a hash will collide — but all
    // of them landing on one would mean the colour was doing no work at all.
    const hues = new Set(['sql', 'oraclesql', 'ansisql', 'http', 'dax', 'mermaid',
      'powershell', 'shellscript', 'csharp', 'markdown'].map(hueFor));
    expect(hues.size).toBeGreaterThanOrEqual(4);
  });
});
