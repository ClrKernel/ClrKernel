import { describe, expect, it } from 'vitest';
import { notebookParameters } from './parameters';
import { parameterValue, readJobsFile, setJobParameter } from './jobsFile';

describe('notebookParameters', () => {
  it('reads the declarations of the // parameters cell only', () => {
    const cells = [
      { kind: 'markdown', source: '# Title' },
      { kind: 'code', source: 'var notAParam = 1;' },
      { kind: 'code', source: '// parameters\nvar region = "us"; // overridden per job\nint days = 7;\nvar ratio = 0.5;\nbool dryRun = true;' },
    ];
    expect(notebookParameters(cells)).toEqual([
      { name: 'region', defaultValue: 'us' },
      { name: 'days', defaultValue: '7' },
      { name: 'ratio', defaultValue: '0.5' },
      { name: 'dryRun', defaultValue: 'true' },
    ]);
  });

  it('is empty when there is no parameters cell', () => {
    expect(notebookParameters([{ kind: 'code', source: 'var x = 1;' }])).toEqual([]);
  });
});

describe('job parameters in the file', () => {
  const text = 'jobs:\n  - name: nightly\n    cron: "0 2 * * *"\n';

  it('writes typed values and reads them back as strings', () => {
    let next = setJobParameter(text, 0, 'region', 'eu');
    next = setJobParameter(next, 0, 'days', '30');
    next = setJobParameter(next, 0, 'dryRun', 'false');
    expect(next).toContain('parameters:');
    expect(next).toContain('days: 30');
    expect(next).toContain('dryRun: false');
    expect(readJobsFile(next).jobs[0].parameters).toEqual({ region: 'eu', days: '30', dryRun: 'false' });
  });

  it('removes the key on empty, and the map when it empties', () => {
    const withOne = setJobParameter(text, 0, 'region', 'eu');
    expect(setJobParameter(withOne, 0, 'region', '')).toBe(text);
  });

  it('infers the value type the runner would', () => {
    expect(parameterValue('5')).toBe(5);
    expect(parameterValue('0.5')).toBe(0.5);
    expect(parameterValue('true')).toBe(true);
    expect(parameterValue('us')).toBe('us');
  });
});
