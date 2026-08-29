import { describe, expect, it } from 'vitest';
import { addJob, readJobsFile, removeJob, setJobField } from './jobsFile';

const file = `# nightly reporting
notebook: ./daily.nb.md
jobs:
  - name: daily          # the important one
    cron: "0 6 * * *"
    parameters:
      region: eu
  - name: weekly
    cron: "0 7 * * 1"
    enabled: false
`;

describe('readJobsFile', () => {
  it('reads the file-level notebook and every job', () => {
    const view = readJobsFile(file);
    expect(view.error).toBeNull();
    expect(view.notebook).toBe('./daily.nb.md');
    expect(view.jobs.map((j) => j.name)).toEqual(['daily', 'weekly']);
    expect(view.jobs[0].cron).toBe('0 6 * * *');
  });

  it('treats an absent `enabled` as true, the way the server does', () => {
    const view = readJobsFile(file);
    expect(view.jobs[0].enabled).toBe(true);
    expect(view.jobs[1].enabled).toBe(false);
  });

  it('names what a job carries that the form does not show', () => {
    // So the UI can point at the YAML tab instead of pretending the job is
    // only what is on screen.
    expect(readJobsFile(file).jobs[0].extras).toEqual(['parameters']);
    expect(readJobsFile(file).jobs[1].extras).toEqual([]);
  });

  it('reports a file it cannot read rather than showing an empty form', () => {
    // An empty form over a broken file invites you to "fix" it by typing into
    // boxes, which would write a new file over the one that needs fixing.
    expect(readJobsFile('jobs:\n  - name: a\n   cron: bad indent\n').error).not.toBeNull();
    expect(readJobsFile('jobs: nope\n').error).toContain('list');
    expect(readJobsFile('- just\n- a list\n').error).toContain('mapping');
  });

  it('and an empty file is not an error, it is an empty file', () => {
    expect(readJobsFile('')).toEqual({ notebook: '', jobs: [], error: null, hasDefaults: false });
  });
});

describe('setJobField', () => {
  it('keeps every comment and everything it did not touch', () => {
    // The whole reason this goes through the document API. A round-trip through
    // a plain object would hand back a file with the notes stripped out.
    const next = setJobField(file, 0, 'cron', '0 8 * * *');
    expect(next).toContain('# nightly reporting');
    expect(next).toContain('# the important one');
    expect(next).toContain('region: eu');
    expect(next).toContain('0 8 * * *');
    expect(next).not.toContain('0 6 * * *');
  });

  it('leaves the other jobs alone', () => {
    expect(setJobField(file, 0, 'cron', '0 8 * * *')).toContain('0 7 * * 1');
  });

  it('clearing a box removes the setting rather than writing an empty one', () => {
    // `cron: ""` is not "no schedule" to the server, it is a schedule it cannot
    // parse. An empty box means absent.
    const next = setJobField(file, 0, 'cron', '');
    expect(next).not.toMatch(/cron:\s*("")?\s*$/m);
    expect(readJobsFile(next).jobs[0].cron).toBe('');
    expect(next).toContain('# the important one');
  });

  it('writes `enabled` only when it is false, because true is the default', () => {
    expect(setJobField(file, 1, 'enabled', true)).not.toContain('enabled');
    expect(setJobField(file, 0, 'enabled', false)).toContain('enabled: false');
  });

  it('refuses to touch a file it could not parse', () => {
    // Rewriting a broken file from a form is how the rest of it disappears.
    const broken = 'jobs:\n  - name: a\n   cron: bad indent\n';
    expect(setJobField(broken, 0, 'cron', '0 8 * * *')).toBe(broken);
  });

  it('round-trips: what is read back is what was set', () => {
    let next = file;
    next = setJobField(next, 1, 'name', 'weekly-eu');
    next = setJobField(next, 1, 'retryCount', '2');
    const view = readJobsFile(next);
    expect(view.jobs[1].name).toBe('weekly-eu');
    expect(view.jobs[1].retryCount).toBe('2');
    expect(view.jobs[0].name).toBe('daily');
  });
});

describe('addJob / removeJob', () => {
  it('appends a job with the one field it cannot do without', () => {
    const view = readJobsFile(addJob(file, 'monthly'));
    expect(view.jobs.map((j) => j.name)).toEqual(['daily', 'weekly', 'monthly']);
  });

  it('starts the list when there is not one yet', () => {
    expect(readJobsFile(addJob('', 'first')).jobs.map((j) => j.name)).toEqual(['first']);
    expect(readJobsFile(addJob('notebook: ./x.nb.md\n', 'first')).notebook).toBe('./x.nb.md');
  });

  it('removes one and leaves the rest, comments included', () => {
    const next = removeJob(file, 1);
    expect(readJobsFile(next).jobs.map((j) => j.name)).toEqual(['daily']);
    expect(next).toContain('# nightly reporting');
    expect(next).toContain('# the important one');
  });

  it('and neither touches a file it could not parse', () => {
    const broken = 'jobs:\n  - name: a\n   cron: bad\n';
    expect(addJob(broken, 'x')).toBe(broken);
    expect(removeJob(broken, 0)).toBe(broken);
  });
});

describe('dependsOn', () => {
  // The file wants a list. Writing the box's text straight in would make
  // `dependsOn: "a, b"` — one job named "a, b", which is a job nobody has.
  it('is a comma-separated box over a YAML list', () => {
    const text = 'jobs:\n  - name: nightly\n    dependsOn:\n      - extract\n      - load\n';
    expect(readJobsFile(text).jobs[0].dependsOn).toBe('extract, load');

    const written = setJobField(text, 0, 'dependsOn', 'extract, transform , load');
    expect(readJobsFile(written).jobs[0].dependsOn).toBe('extract, transform, load');
    expect(written).toMatch(/- transform/);
  });

  it('is removed rather than emptied when the box is cleared', () => {
    const text = 'jobs:\n  - name: nightly\n    dependsOn: [a]\n';
    expect(setJobField(text, 0, 'dependsOn', '')).not.toMatch(/dependsOn/);
  });

  // It used to land in `extras`, which told the reader to go to the YAML tab for
  // something the form now shows.
  it('is no longer something the form hides', () => {
    const text = 'jobs:\n  - name: nightly\n    dependsOn: [a]\n';
    expect(readJobsFile(text).jobs[0].extras).toEqual([]);
  });
});
