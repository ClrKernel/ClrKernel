import { useState } from 'react';
import { Button } from '@/components/ui/button';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { api } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

/**
 * The values a notebook resolves by name, per branch.
 *
 * <p>Names and whether each has one — never the value, not even masked. The same
 * rule <c>ConnectionView</c> follows for a connection's password, and for the same
 * reason: a page that can show a secret is a page that can leak one.</p>
 *
 * <p>Per branch because that is how they resolve. <code>OPENAI</code> on test and
 * <code>OPENAI</code> on prod are two secrets, and a notebook running on prod cannot
 * reach test's — so the branch picker is the substance of the page rather than a
 * filter over it.</p>
 */
export function SecretsSection() {
  const [branch, setBranch] = useState('mine');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const { data: branches } = usePolling(() => api.branches(), null, []);
  const { data, error: loadError, reload } = usePolling(
    () => api.secrets(branch), null, [branch]);

  function show(next: string) {
    // "OPENAI saved." left standing over another branch's table is a lie about
    // which branch it was saved on.
    setNotice(null);
    setError(null);
    setBranch(next);
  }

  // Yours, and the environments. Somebody else's personal branch is left out
  // because the server refuses it — theirs to manage, whatever role you hold.
  const choices = (branches?.branches ?? []).filter((b) => b.mine || b.owner == null);

  async function run(action: () => Promise<unknown>, done: string) {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      await action();
      setNotice(done);
      reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function add() {
    const name = prompt(
      'Name of the secret, as a cell asks for it — Resolve("OPENAI").\n\n'
      + 'Letters, digits and underscores: it reaches the kernel as an environment '
      + 'variable, and anything else would fold into one.');
    if (name == null || !name.trim()) {
      return;
    }
    replace(name.trim(), `${name.trim()} saved.`);
  }

  function replace(name: string, done: string) {
    const value = prompt(`Value for ${name} on ${label}. Stored, and never shown again.`);
    if (value) {
      run(() => api.setSecret(branch, name, value), done);
    }
  }

  const label = choices.find((b) => b.id === branch)?.label ?? branch;
  const secrets = data?.secrets ?? [];

  return (
    <section className="settings-section">
      <p className="mb-3 max-w-[78ch] text-base text-muted-foreground">
        Values a notebook asks for by name — <code>new SecretStore().Resolve("OPENAI")</code> in
        a C# cell, and the password behind a connection. Kept per branch: the same name on
        your branch, on test and on prod is three secrets with three values, and a cell only
        ever sees its own branch's. A job promoted to production says the secret is missing
        until production has its own, rather than quietly running on test's.
      </p>
      <p className="mb-4 max-w-[78ch] text-base text-muted-foreground">
        A kernel is handed its branch's secrets when it starts, so a notebook that is
        already open keeps the set it started with. Restart its kernel after adding one
        here, or the cell will say the secret is missing when the branch has it.
      </p>

      <ErrorBanner error={error ?? loadError} />
      {notice && <Alert className="mb-3"><AlertDescription>{notice}</AlertDescription></Alert>}
      {data && !data.canPersist && (
        <Alert variant="destructive" className="mb-3">
          <AlertDescription>
            This server has nowhere to keep a secret, so none can be saved here. Give it a
            credential store, or start it with <code>--secret-store file</code>.
          </AlertDescription>
        </Alert>
      )}

      <div className="mb-4 flex items-center gap-2">
        <Select value={branch} onValueChange={show}>
          <SelectTrigger size="sm" aria-label="Branch" className="w-56">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {choices.map((b) => (
              <SelectItem key={b.id} value={b.id}>{b.label}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button size="xs" onClick={add} disabled={busy || !data?.canPersist}>
          Add a secret
        </Button>
      </div>

      <div className="table-box max-w-[820px]">
        <table className="table">
          <thead>
            <tr><th>Name</th><th>Value</th><th>Added by</th><th /></tr>
          </thead>
          <tbody>
            {secrets.map((secret) => (
              <tr key={secret.name}>
                <td className="font-mono text-xs">{secret.name}</td>
                <td>
                  {/* "set" is the whole of what this page knows. Reading one back is
                      not something it can do, and that is the point. */}
                  {secret.isSet
                    ? <span className="text-muted-foreground">set</span>
                    : <span className="text-destructive">not set</span>}
                </td>
                <td className="text-muted-foreground">{secret.createdBy ?? '—'}</td>
                <td className="text-right whitespace-nowrap">
                  <Button
                    size="xs"
                    variant="outline"
                    disabled={busy || !data?.canPersist}
                    onClick={() => replace(secret.name, `${secret.name} replaced.`)}
                  >
                    Replace
                  </Button>{' '}
                  <Button
                    size="xs"
                    variant="outline"
                    disabled={busy}
                    onClick={() => {
                      if (confirm(`Forget ${secret.name} on ${label}? Cells asking for it will fail.`)) {
                        run(() => api.deleteSecret(branch, secret.name), `${secret.name} forgotten.`);
                      }
                    }}
                  >
                    Forget
                  </Button>
                </td>
              </tr>
            ))}
            {secrets.length === 0 && (
              <tr>
                <td colSpan={4} className="text-muted-foreground">
                  Nothing on {label} yet. A secret set from a shell will not appear here until
                  it is named — a credential store cannot be listed, so this table is the only
                  record of what exists.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </section>
  );
}
