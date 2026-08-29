import { registerJobsYaml } from './jobsYaml';
import type { JobsSchemaDocument } from '../jobsSchema';

/**
 * Fetches the jobs-file schema the server publishes and turns on completion for
 * it, once per page load.
 *
 * The server builds this schema from the same `JobsSchema` its validator uses, so
 * the editor and the push gate cannot come to different conclusions about a file.
 * Shipping a second copy in the client is exactly how they would.
 *
 * Cached as the promise rather than the result: two jobs files opened quickly
 * would otherwise both see "not loaded yet" and fetch.
 */
let configured: Promise<boolean> | null = null;

export function enableJobsSchema(): Promise<boolean> {
  configured ??= fetch('/api/jobs/schema')
    .then((response) =>
      response.ok ? response.json() : Promise.reject(new Error(String(response.status))))
    .then((schema: JobsSchemaDocument) => {
      registerJobsYaml(schema);
      return true;
    })
    .catch(() =>
      // An old server, or one that cannot answer. Plain YAML highlighting is a
      // worse editor, not a broken one — and the server validates on save and
      // refuses the push either way, so nothing unsafe reaches test.
      false);
  return configured;
}
