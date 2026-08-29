import { describe, expect, it } from 'vitest';
import { completionsAt, type JobsSchemaDocument } from './jobsSchema';

/** The shape the server actually publishes, trimmed to what this reads. */
const schema: JobsSchemaDocument = {
  properties: {
    notebook: { type: 'string', description: 'shared notebook', examples: ['./daily.nb.md'] },
    defaults: { $ref: '#/definitions/entry' },
    jobs: { type: 'array', description: 'the jobs' },
  },
  definitions: {
    entry: {
      properties: {
        name: { type: 'string', description: 'unique' },
        notebook: { type: 'string', description: 'notebook to run' },
        cron: { type: 'string', description: 'five-field cron', examples: ['0 6 * * *'] },
        enabled: { type: 'boolean', description: 'default true' },
        notify: { $ref: '#/definitions/notify' },
      },
    },
    notify: {
      properties: {
        onFailure: { type: 'array', description: 'channels on failure' },
        onSuccess: { type: 'array', description: 'channels on success' },
      },
    },
  },
};

const names = (above: string[], before: string) =>
  completionsAt(schema, above, before).map((f) => f.name);

describe('completionsAt', () => {
  it('offers the file-level settings at the left margin', () => {
    expect(names([], '')).toEqual(['notebook', 'defaults', 'jobs']);
  });

  it('offers job settings inside a jobs entry', () => {
    expect(names(['jobs:', '  - name: daily'], '    ')).toContain('cron');
    expect(names(['jobs:', '  - name: daily'], '    ')).not.toContain('jobs');
  });

  it('and on the `- ` that opens one', () => {
    expect(names(['jobs:'], '  - ')).toContain('name');
  });

  it('offers notify settings inside notify', () => {
    expect(names(['jobs:', '  - name: daily', '    notify:'], '      '))
      .toEqual(['onFailure', 'onSuccess']);
  });

  it('offers job settings inside defaults, which is the same shape', () => {
    expect(names(['defaults:'], '  ')).toContain('cron');
  });

  it('leaves out what this block already has', () => {
    const offered = names(['jobs:', '  - name: daily', '    cron: "0 6 * * *"'], '    ');
    expect(offered).not.toContain('name');
    expect(offered).not.toContain('cron');
    expect(offered).toContain('enabled');
  });

  it('but not what a *previous* job had', () => {
    // The `- ` starts a fresh set. Without this, the second job is offered
    // nothing because the first one used the keys.
    const offered = names(
      ['jobs:', '  - name: one', '    cron: "0 6 * * *"', '  - name: two'], '    ');
    expect(offered).toContain('cron');
    expect(offered).not.toContain('name');
  });

  it('says nothing once a value is being typed', () => {
    // After `cron: ` the answer is a cron expression, which this knows nothing
    // about — and a list of keys there would be actively in the way.
    expect(names(['jobs:', '  - name: daily'], '    cron: 0 6')).toEqual([]);
  });

  it('and nothing in a comment', () => {
    expect(names([], '# ')).toEqual([]);
  });

  it('ignores blank lines and comments when working out where it is', () => {
    expect(names(['jobs:', '  - name: daily', '', '    # a note', ''], '    '))
      .toContain('cron');
  });

  it('carries the description and the example through for the hover', () => {
    const cron = completionsAt(schema, ['jobs:', '  - name: daily'], '    ')
      .find((f) => f.name === 'cron');
    expect(cron?.description).toBe('five-field cron');
    expect(cron?.example).toBe('0 6 * * *');
  });

  it('survives a schema it does not recognise', () => {
    expect(completionsAt({}, [], '')).toEqual([]);
  });
});
