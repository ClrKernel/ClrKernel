import { describe, expect, it } from 'vitest';
import { fileBadge } from './fileIcon';

describe('fileBadge', () => {
  it('knows the files this tree actually holds', () => {
    expect(fileBadge('daily.nb.md')).toEqual({ label: 'M↓', tone: 'notebook' });
    expect(fileBadge('old.ipynb')).toEqual({ label: 'NB', tone: 'notebook' });
    expect(fileBadge('scratch.dib')).toEqual({ label: 'DIB', tone: 'notebook' });
    expect(fileBadge('setup.csx')).toEqual({ label: 'C#', tone: 'code' });
    expect(fileBadge('daily.jobs.yaml')).toEqual({ label: 'JOB', tone: 'config' });
  });

  it('matches the longest extension, not the last dot', () => {
    // The pair that discriminates: `.jobs.yaml` has to be found before `.yaml`,
    // or every jobs file in the tree is labelled as plain configuration.
    expect(fileBadge('daily.jobs.yaml').label).toBe('JOB');
    expect(fileBadge('config.yaml').label).toBe('YML');
  });

  it('does not care how the name is cased', () => {
    expect(fileBadge('DAILY.NB.MD').tone).toBe('notebook');
    expect(fileBadge('Setup.CSX').label).toBe('C#');
  });

  it('gives a file nobody planned for its own extension', () => {
    expect(fileBadge('archive.parquet')).toEqual({ label: 'PAR', tone: 'code' });
    expect(fileBadge('notes.txt').label).toBe('TXT');
  });

  it('never renders an empty badge', () => {
    expect(fileBadge('LICENSE').label).toBe('LIC');
    expect(fileBadge('.gitignore').label).toBe('GIT');
    expect(fileBadge('').label).toBe('?');
  });
});
