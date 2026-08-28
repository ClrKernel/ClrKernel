import { useEffect, useMemo, useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  api,
  type ApiConnection,
  type ApiConnectionProvider,
  type ApiConnectionSave,
  type ConnectionScope,
} from '../api';
import { isSecret } from '../connectionDirective';
import { useIsServerAdmin } from '../sessionContext';
import { ErrorBanner } from './common';
import { Field } from './ConnectionWizard';
import { Modal } from './Modal';

/**
 * Creating or editing a saved connection.
 *
 * The settings are not listed here: they come from the provider's own descriptor,
 * the same one the notebook connection wizard renders, so a connection type added
 * later appears with no change to this file. What *is* here is everything the
 * descriptor cannot know — which list it goes in, how its password is kept, and
 * the second credential that decides whether anybody but an admin may run
 * against it.
 */
export function ConnectionForm({
  connection, onSaved, onClose, onDeleted,
}: {
  /** null when creating. */
  connection: ApiConnection | null;
  onSaved: (saved: ApiConnection, close?: boolean) => void;
  onClose: () => void;
  onDeleted: (id: string) => void;
}) {
  const isAdmin = useIsServerAdmin();
  const [providers, setProviders] = useState<ApiConnectionProvider[] | null>(null);
  const [canPersist, setCanPersist] = useState(true);
  const [secretHelp, setSecretHelp] = useState<string | null>(null);
  const [privateReadOnly, setPrivateReadOnly] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [testing, setTesting] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  // What the server holds, which starts as the prop and changes underneath us when
  // Test has to save a new connection before it can open one.
  const [stored, setStored] = useState(connection);

  const [name, setName] = useState(connection?.name ?? '');
  const [scope, setScope] = useState<ConnectionScope>(
    connection?.scope ?? (isAdmin ? 'shared' : 'private'));
  const [type, setType] = useState(connection?.type ?? '');
  const [values, setValues] = useState<Record<string, string | boolean | undefined>>(
    connection?.settings ?? {});
  const [password, setPassword] = useState('');
  const [secretRef, setSecretRef] = useState(connection?.secretRef ?? '');
  const [prompt, setPrompt] = useState(connection?.promptForPassword ?? false);
  const [readOnlyUser, setReadOnlyUser] = useState(connection?.readOnlyUser ?? '');
  const [readOnlyPassword, setReadOnlyPassword] = useState('');
  const [readOnlySecretRef, setReadOnlySecretRef] = useState('');
  const [timeoutSeconds, setTimeout] = useState(String(connection?.timeoutSeconds ?? 30));
  const [rowCap, setRowCap] = useState(String(connection?.rowCap ?? 10000));

  useEffect(() => {
    api.connectionProviderSchema()
      .then((reply) => {
        setProviders(reply.providers);
        setCanPersist(reply.canPersistSecrets);
        setSecretHelp(reply.secretHelp);
        setPrivateReadOnly(reply.privateConnectionsReadOnly);
        setType((current) => current || reply.providers[0]?.type || '');
      })
      .catch((e) => setError((e as Error).message));
  }, []);

  const provider = providers?.find((p) => p.type === type) ?? null;
  // The credential settings are rendered by the block below, which knows about
  // storing a password; the descriptor only knows it is a secret reference.
  const plain = useMemo(
    () => provider?.settings.filter((s) => !isSecret(s.kind)) ?? [],
    [provider]);
  const integrated = String(values.auth ?? '') === 'integrated';

  /** The form as the API wants it. Built for Save, and again for Test. */
  function draft(): ApiConnectionSave {
    const body: ApiConnectionSave = {
      name,
      scope,
      type,
      settings: Object.fromEntries(
        Object.entries(values)
          .filter(([, v]) => v != null && String(v).length > 0)
          .map(([k, v]) => [k, String(v)])),
      promptForPassword: prompt,
      readOnlyUser: readOnlyUser || undefined,
      timeoutSeconds: Number(timeoutSeconds) || 30,
      rowCap: Number(rowCap) || 10000,
    };
    if (password) {
      body.password = password;
    }
    if (!canPersist && secretRef) {
      body.secretRef = secretRef;
    }
    if (readOnlyPassword) {
      body.readOnlyPassword = readOnlyPassword;
    }
    if (!canPersist && readOnlySecretRef) {
      body.readOnlySecretRef = readOnlySecretRef;
    }
    return body;
  }

  async function save() {
    setSaving(true);
    setError(null);
    try {
      onSaved(await api.saveConnection(stored?.id ?? null, draft()));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  /**
   * Testing opens what the server has stored, not what is on screen — the
   * password may be a reference it has to resolve. So a connection that has never
   * been saved is saved first, and the dialog stays open on the result: "save it
   * first, then reopen it to test" was a step nobody could guess was required.
   */
  async function test() {
    setSaving(true);
    setTesting('Connecting…');
    try {
      let target = stored;
      if (target == null) {
        target = await api.saveConnection(null, draft());
        setStored(target);
        // The list needs the new row; the dialog is still in use, so it stays.
        onSaved(target, false);
      }
      const reply = await api.testConnection(target.id, password || undefined);
      setTesting(reply.ok ? 'Connected.' : reply.error);
    } catch (e) {
      setTesting((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (stored == null || !confirm(`Delete the connection '${stored.name}'?`)) {
      return;
    }
    try {
      await api.deleteConnection(stored.id);
      onDeleted(stored.id);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <Modal title={stored == null ? 'New connection' : stored.name} onClose={onClose}>
      <ErrorBanner error={error} />

      <div className="wizard-fields">
        <label className="form-field">
          <span>Name<span className="wizard-required"> *</span></span>
          <input value={name} onChange={(e) => setName(e.target.value)}
            placeholder="warehouse" />
          <em className="text-base text-muted-foreground">
            What a notebook cell references. It has to stay put, so it is not a label.
          </em>
        </label>

        <label className="form-field">
          <span>Visible to</span>
          <select
            value={scope}
            // A connection cannot move between the lists after it is created:
            // publishing somebody's credential to the whole server on a dropdown
            // change is not an undo away, and moving one out breaks every
            // notebook that names it.
            disabled={stored != null}
            onChange={(e) => setScope(e.target.value as ConnectionScope)}
          >
            {isAdmin && <option value="shared">Everyone (shared)</option>}
            <option value="private">Only me</option>
          </select>
          <em className="text-base text-muted-foreground">
            {scope === 'shared'
              ? 'Managed by server admins and visible to everybody.'
              : 'Invisible to everyone else, server admins included, and never committed.'}
          </em>
        </label>

        {providers != null && providers.length > 1 && (
          <label className="form-field">
            <span>Type</span>
            <select value={type} onChange={(e) => setType(e.target.value)} disabled={stored != null}>
              {providers.map((p) => (
                <option key={p.type} value={p.type}>{p.displayName}</option>
              ))}
            </select>
          </label>
        )}

        {plain.map((setting) => (
          <Field
            key={setting.name}
            setting={setting}
            value={values[setting.name]}
            onChange={(v) => setValues((current) => ({ ...current, [setting.name]: v }))}
          />
        ))}
      </div>

      {provider != null && provider.queryable === false && (
        <Alert>
          <AlertDescription>
            This server can save a {provider.displayName} connection so notebooks can name it,
            and the kernel opens it when one runs. It cannot browse or query it here — that
            provider's driver is loaded into a kernel session rather than into this server.
          </AlertDescription>
        </Alert>
      )}

      {integrated && (
        <Alert>
          <AlertDescription>
            Integrated authentication signs in as the <strong>server process</strong>, not as you.
            Everyone sharing this connection acts as that account, and that is the name the
            database's audit trail will show.
          </AlertDescription>
        </Alert>
      )}

      {/* "Credential", not "Password": under a field list that already ends in
          Encrypt and Trust server certificate, a heading called Password reads
          like one more setting rather than the start of a section — and there is
          a field called Password inside it. */}
      <h3>Credential</h3>
      {secretHelp && (
        <Alert>
          <AlertDescription>{secretHelp}</AlertDescription>
        </Alert>
      )}
      <div className="wizard-fields">
        <label className="form-field checkbox">
          <input type="checkbox" checked={prompt} onChange={(e) => setPrompt(e.target.checked)} />
          <span>
            Do not store it — ask each session
            {stored?.secretConfigured && !prompt && (
              <Badge variant="outline" className="font-normal">configured</Badge>
            )}
          </span>
        </label>
        {!prompt && canPersist && (
          <label className="form-field">
            <span>
              Password
              {stored?.secretConfigured && (
                <Badge variant="outline" className="font-normal">configured</Badge>
              )}
            </span>
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
              placeholder={stored?.secretConfigured ? 'leave blank to keep the stored one' : ''} />
            <em className="text-base text-muted-foreground">
              Kept in this server's credential store. It is never written to the connection
              itself, never sent back to a browser, and never lands in a notebook.
            </em>
          </label>
        )}
        {!prompt && !canPersist && (
          <label className="form-field">
            <span>Secret name</span>
            <input value={secretRef} onChange={(e) => setSecretRef(e.target.value)}
              placeholder="the name of a secret, not the password" />
          </label>
        )}
      </div>

      {/* Shared connections always need one; private ones do too when the install
          has asked for it. Rendering this only for shared meant that on such an
          install a private connection had no field anywhere to make it runnable. */}
      {(scope === 'shared' || privateReadOnly) && (
        <>
          <h3>Read-only login</h3>
          <p className="text-base text-muted-foreground">
            {scope === 'shared'
              ? 'Everyone below a server admin runs as this login. Without it they cannot run '
                + 'against this connection at all — which is honest: no amount of reading the '
                + 'SQL makes a writable login read-only, so the second credential is the '
                + 'boundary.'
              : 'This server requires a read-only login on every connection, private ones '
                + 'included. Without one this connection cannot be run at all.'}
          </p>
          <div className="wizard-fields">
            <label className="form-field">
              <span>User</span>
              <input value={readOnlyUser} onChange={(e) => setReadOnlyUser(e.target.value)}
                placeholder="reader" />
            </label>
            {readOnlyUser && canPersist && (
              <label className="form-field">
                <span>
                  Password
                  {stored?.readOnlySecretConfigured && (
                    <Badge variant="outline" className="font-normal">configured</Badge>
                  )}
                </span>
                <Input type="password" value={readOnlyPassword}
                  onChange={(e) => setReadOnlyPassword(e.target.value)}
                  placeholder={stored?.readOnlySecretConfigured
                    ? 'leave blank to keep the stored one' : ''} />
              </label>
            )}
            {/* The same escape hatch the primary credential has. Without it, a
                server with no OS credential store could never make any shared
                connection runnable by a non-admin. */}
            {readOnlyUser && !canPersist && (
              <label className="form-field">
                <span>Secret name</span>
                <input value={readOnlySecretRef}
                  onChange={(e) => setReadOnlySecretRef(e.target.value)}
                  placeholder="the name of a secret, not the password" />
              </label>
            )}
          </div>
        </>
      )}

      <h3>Limits</h3>
      <div className="wizard-fields">
        <label className="form-field">
          <span>Query timeout (seconds)</span>
          <input value={timeoutSeconds} onChange={(e) => setTimeout(e.target.value)} inputMode="numeric" />
        </label>
        <label className="form-field">
          <span>Row cap</span>
          <input value={rowCap} onChange={(e) => setRowCap(e.target.value)} inputMode="numeric" />
          <em className="text-base text-muted-foreground">
            A SELECT * against a fact table should not be able to take down the browser tab.
          </em>
        </label>
      </div>

      {testing && <p className="text-base text-muted-foreground">{testing}</p>}

      <div className="flex items-center gap-2">
        <Button size="sm" disabled={saving || name.trim().length === 0} onClick={save}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
        <Button variant="outline" size="sm" onClick={test}
          disabled={saving || name.trim().length === 0}>
          {stored == null ? 'Save & test' : 'Test'}
        </Button>
        <span className="spacer" />
        {stored != null && (
          <Button variant="outline" size="sm"
            className="text-destructive hover:bg-destructive/10 hover:text-destructive"
            onClick={remove}>
            Delete
          </Button>
        )}
        <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
      </div>
    </Modal>
  );
}
