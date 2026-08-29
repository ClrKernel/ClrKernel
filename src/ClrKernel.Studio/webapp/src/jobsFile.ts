import { parseDocument, isMap, isScalar, isSeq, type Document } from 'yaml';

/**
 * A `*.jobs.yaml` as a form can show it, and edits written back into the same
 * text the YAML tab is showing.
 *
 * Overview and YAML are two views of one file, not two models of it. Everything
 * downstream — autosave, diff, push, promote — already works on the file, so an
 * edit here has to produce *text*, and the buffer stays the single truth.
 *
 * Which is why this goes through `yaml`'s document API rather than
 * parse-to-object-and-re-serialise: that would round-trip a file through a plain
 * object and hand it back stripped of every comment and reordered to whatever the
 * serialiser prefers. Someone who wrote a note above a job would lose it by
 * toggling a checkbox. The document API edits in place and leaves the rest of the
 * file exactly as it was.
 *
 * React-free and testable, because the edits are the risky part: a form that
 * silently mangles somebody's file is worse than no form.
 */

/** One job as the Overview form shows it. Strings, because inputs hold strings. */
export interface JobView {
  name: string;
  notebook: string;
  cron: string;
  enabled: boolean;
  timeoutSeconds: string;
  retryCount: string;
  /** Comma-separated on the form, a YAML list in the file. */
  dependsOn: string;
  /** Set when this job carries something the form does not show. */
  extras: string[];
}

export interface JobsFileView {
  /** The file-level `notebook:`, inherited by jobs that set none. */
  notebook: string;
  jobs: JobView[];
  /** Present when the text could not be read as a jobs file at all. */
  error: string | null;
  /** True when the file has `defaults:`, which the form does not edit. */
  hasDefaults: boolean;
}

/** Fields the Overview form knows how to show and write. */
const SHOWN = [
  'name', 'notebook', 'cron', 'enabled', 'timeoutSeconds', 'retryCount', 'dependsOn',
];

/** `dependsOn` is a list in the file and one comma-separated box on the form. */
function joinList(node: unknown): string {
  return isSeq(node)
    ? node.items.map((item) => (isScalar(item) ? String(item.value ?? '') : '')).filter(Boolean).join(', ')
    : scalar(node);
}

function splitList(value: string): string[] {
  return value.split(',').map((part) => part.trim()).filter(Boolean);
}

function scalar(node: unknown): string {
  return node == null || typeof node === 'object' ? '' : String(node);
}

export function readJobsFile(text: string): JobsFileView {
  const empty: JobsFileView = { notebook: '', jobs: [], error: null, hasDefaults: false };
  let document: Document.Parsed;
  try {
    document = parseDocument(text ?? '');
  } catch (e) {
    return { ...empty, error: (e as Error).message };
  }
  if (document.errors.length > 0) {
    return { ...empty, error: document.errors[0].message };
  }
  const root = document.contents;
  if (root == null) {
    return empty;
  }
  if (!isMap(root)) {
    return { ...empty, error: 'A jobs file is a mapping with a `jobs:` list.' };
  }
  const jobs = root.get('jobs', true);
  if (jobs != null && !isSeq(jobs)) {
    return { ...empty, error: '`jobs:` is a list, one entry per job.' };
  }

  return {
    notebook: scalar(document.getIn(['notebook'])),
    hasDefaults: root.has('defaults'),
    error: null,
    jobs: (jobs?.items ?? []).map((item, index) => ({
      name: scalar(document.getIn(['jobs', index, 'name'])),
      notebook: scalar(document.getIn(['jobs', index, 'notebook'])),
      cron: scalar(document.getIn(['jobs', index, 'cron'])),
      // Absent means true — the same default the server applies.
      enabled: document.getIn(['jobs', index, 'enabled']) !== false,
      timeoutSeconds: scalar(document.getIn(['jobs', index, 'timeoutSeconds'])),
      retryCount: scalar(document.getIn(['jobs', index, 'retryCount'])),
      dependsOn: joinList(document.getIn(['jobs', index, 'dependsOn'], true)),
      // What this job has that the form does not show, so the UI can say the
      // YAML tab is where the rest of it lives rather than pretending there is
      // nothing else.
      extras: isMap(item)
        ? item.items
            // The key is a node, not a string — `String(node)` on a Scalar gives
            // its source text, quotes and all, so read `.value`.
            .map((pair) => (isScalar(pair.key) ? String(pair.key.value ?? '') : ''))
            .filter((key) => key !== '' && !SHOWN.includes(key))
        : [],
    })),
  };
}

/**
 * One field of one job, written back into the text.
 *
 * An empty string removes the key rather than writing `cron: ""` — an empty
 * setting and an absent one mean different things to the server, and what a
 * cleared box means is absent. `enabled` is the exception: it is written only
 * when false, because true is the default and `enabled: true` on every job is
 * noise nobody typed.
 */
export function setJobField(
  text: string, index: number, key: keyof JobView, value: string | boolean,
): string {
  const document = parseDocument(text ?? '');
  if (document.errors.length > 0) {
    // Refuse rather than guess. The YAML tab is where a broken file gets fixed,
    // and rewriting one from a form is how the rest of it disappears.
    return text;
  }
  const path = ['jobs', index, key];
  if (value === '' || value === true) {
    document.deleteIn(path);
  } else if (key === 'dependsOn' && typeof value === 'string') {
    // A list, not the string the box holds — `dependsOn: "a, b"` is one job
    // called "a, b", which is a job that does not exist.
    document.setIn(path, splitList(value));
  } else {
    document.setIn(path, value);
  }
  return String(document);
}

/** A job appended to the file, with the one field it cannot do without. */
export function addJob(text: string, name: string): string {
  const document = parseDocument(text?.trim() ? text : 'jobs:\n');
  if (document.errors.length > 0) {
    return text;
  }
  const jobs = document.get('jobs', true);
  if (!isSeq(jobs)) {
    // `set('jobs', [])` stores a plain array, and `setIn` then refuses it —
    // "Expected YAML collection". Setting the whole list at once lets the
    // document build proper nodes.
    document.set('jobs', [{ name }]);
  } else {
    document.setIn(['jobs', jobs.items.length], { name });
  }
  return String(document);
}

/** Removes a job. The caller confirms — this does not ask. */
export function removeJob(text: string, index: number): string {
  const document = parseDocument(text ?? '');
  if (document.errors.length > 0) {
    return text;
  }
  document.deleteIn(['jobs', index]);
  return String(document);
}
