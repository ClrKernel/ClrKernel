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
import {
  CheckboxField, Field, FieldGrid, FieldSection, SelectField,
} from '@/components/ui/field';
import { unmet } from '../connectionFields';
import { useIsServerAdmin } from '../sessionContext';
import { ErrorBanner } from './common';
import { Fields } from './ConnectionWizard';
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
  onSaved: (saved: ApiConnection) => void;
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
        // Only when there is nothing to choose. Landing on whichever provider
        // happened to be first means a saved connection can be the wrong type
        // because nobody noticed the field was already filled in.
        setType((current) =>
          current || (reply.providers.length === 1 ? reply.providers[0].type : ''));
      })
      .catch((e) => setError((e as Error).message));
  }, []);

  const provider = providers?.find((p) => p.type === type) ?? null;
  // The credential settings are rendered by the block below, which knows about
  // storing a password; the descriptor only knows it is a secret reference.
  const plain = useMemo(
    () => (provider == null ? null : {
      ...provider,
      settings: provider.settings.filter((s) => !isSecret(s.kind)),
    }),
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
      onSaved(await api.saveConnection(connection?.id ?? null, draft()));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  /**
   * Nothing is written. A connection that does not answer is one you probably do
   * not want saved, so an unsaved draft is tested as it stands; an existing one
   * still tests what the server holds, because its password may be a reference
   * only the server can resolve.
   */
  async function test() {
    // Before the round trip: an empty field is not something a database can
    // answer, and its answer to one is misleading — a missing user comes back as
    // "Login failed for user ''", which reads like a wrong password.
    const gaps = unmet(provider, values);
    if (gaps.length > 0) {
      setTesting(`Still needed: ${gaps.join(', ')}.`);
      return;
    }
    setSaving(true);
    setTesting('Connecting…');
    try {
      const reply = connection == null
        ? await api.testDraftConnection(draft())
        : await api.testConnection(connection.id, password || undefined);
      setTesting(reply.ok ? 'Connected.' : reply.error);
    } catch (e) {
      setTesting((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (connection == null || !confirm(`Delete the connection '${connection.name}'?`)) {
      return;
    }
    try {
      await api.deleteConnection(connection.id);
      onDeleted(connection.id);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <Modal
      title={connection == null ? 'New connection' : connection.name}
      onClose={onClose}
      footer={
        <>
          <Button size="sm" disabled={saving || name.trim().length === 0 || type === ''} onClick={save}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Button variant="outline" size="sm" onClick={test} disabled={saving || type === ''}>
            Test
          </Button>
          <span className="flex-1" />
          {connection != null && (
            <Button variant="outline" size="sm"
              className="text-destructive hover:bg-destructive/10 hover:text-destructive"
              onClick={remove}>
              Delete
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <ErrorBanner error={error} />

      <FieldGrid>
        <Field
          label="Name"
          required
          hint="What a notebook cell references. It has to stay put, so it is not a label."
        >
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="warehouse" />
        </Field>

        <SelectField
          label="Visible to"
          value={scope}
          onChange={(value) => setScope(value as ConnectionScope)}
          // A connection cannot move between the lists after it is created:
          // publishing somebody's credential to the whole server on a dropdown
          // change is not an undo away, and moving one out breaks every notebook
          // that names it.
          disabled={connection != null}
          options={[
            ...(isAdmin ? [{ value: 'shared', label: 'Everyone (shared)' }] : []),
            { value: 'private', label: 'Only me' },
          ]}
          hint={scope === 'shared'
            ? 'Managed by server admins and visible to everybody.'
            : 'Invisible to everyone else, server admins included, and never committed.'}
        />

        {providers != null && providers.length > 1 && (
          <SelectField
            label="Type"
            required
            value={type}
            onChange={setType}
            placeholder="(Select a type)"
            disabled={connection != null}
            options={providers.map((p) => ({ value: p.type, label: p.displayName }))}
          />
        )}

        {plain != null && (
          <Fields
            provider={plain}
            values={values}
            onChange={(name, v) => setValues((current) => ({ ...current, [name]: v }))}
          />
        )}
      </FieldGrid>

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

      {/* Everything below is about a connection of some type: a password, a
          read-only login and a row cap all presuppose one. Asking for them
          before the type is chosen is asking about nothing in particular. */}
      {provider != null && (
        <>
        {/* "Credential", not "Password": under a field list that already ends in
            Encrypt and Trust server certificate, a heading called Password reads
            like one more setting rather than the start of a section — and there is
            a field called Password inside it. */}
        <FieldSection title="Credential">
          {secretHelp && (
            <Alert>
              <AlertDescription>{secretHelp}</AlertDescription>
            </Alert>
          )}
          {/* Outside the grid: a checkbox is one line tall and a field is three, so
              sharing a row leaves the checkbox stranded at the top of a tall cell. */}
          <CheckboxField
            label={
              <>
                Do not store it — ask each session
                {connection?.secretConfigured && !prompt && (
                  <Badge variant="outline" className="ml-2 font-normal">configured</Badge>
                )}
              </>
            }
            checked={prompt}
            onChange={setPrompt}
          />
          <FieldGrid>
          {!prompt && canPersist && (
            <Field
              label={
                <>
                  Password
                  {connection?.secretConfigured && (
                    <Badge variant="outline" className="ml-2 font-normal">configured</Badge>
                  )}
                </>
              }
              hint={"Kept in this server's credential store. It is never written to the connection "
                + 'itself, never sent back to a browser, and never lands in a notebook.'}
            >
              <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
                placeholder={connection?.secretConfigured ? 'leave blank to keep the connection one' : ''} />
            </Field>
          )}
          {!prompt && !canPersist && (
            <Field label="Secret name">
              <Input value={secretRef} onChange={(e) => setSecretRef(e.target.value)}
                placeholder="the name of a secret, not the password" />
            </Field>
          )}
          </FieldGrid>
        </FieldSection>

        {/* Shared connections always need one; private ones do too when the install
            has asked for it. Rendering this only for shared meant that on such an
            install a private connection had no field anywhere to make it runnable. */}
        {(scope === 'shared' || privateReadOnly) && (
          <FieldSection
            title="Read-only login"
            description={scope === 'shared'
              ? 'Everyone below a server admin runs as this login. Without it they cannot run '
                + 'against this connection at all — which is honest: no amount of reading the '
                + 'SQL makes a writable login read-only, so the second credential is the '
                + 'boundary.'
              : 'This server requires a read-only login on every connection, private ones '
                + 'included. Without one this connection cannot be run at all.'}
          >
            <FieldGrid>
              <Field label="User">
                <Input value={readOnlyUser} onChange={(e) => setReadOnlyUser(e.target.value)}
                  placeholder="reader" />
              </Field>
              {readOnlyUser && canPersist && (
                <Field
                  label={
                    <>
                      Password
                      {connection?.readOnlySecretConfigured && (
                        <Badge variant="outline" className="ml-2 font-normal">configured</Badge>
                      )}
                    </>
                  }
                >
                  <Input type="password" value={readOnlyPassword}
                    onChange={(e) => setReadOnlyPassword(e.target.value)}
                    placeholder={connection?.readOnlySecretConfigured
                      ? 'leave blank to keep the connection one' : ''} />
                </Field>
              )}
              {/* The same escape hatch the primary credential has. Without it, a
                  server with no OS credential store could never make any shared
                  connection runnable by a non-admin. */}
              {readOnlyUser && !canPersist && (
                <Field label="Secret name">
                  <Input value={readOnlySecretRef}
                    onChange={(e) => setReadOnlySecretRef(e.target.value)}
                    placeholder="the name of a secret, not the password" />
                </Field>
              )}
            </FieldGrid>
          </FieldSection>
        )}

        <FieldSection title="Limits">
          <FieldGrid>
          <Field label="Query timeout (seconds)">
            <Input value={timeoutSeconds} onChange={(e) => setTimeout(e.target.value)}
              inputMode="numeric" />
          </Field>
          <Field
            label="Row cap"
            hint="A SELECT * against a fact table should not be able to take down the browser tab."
          >
            <Input value={rowCap} onChange={(e) => setRowCap(e.target.value)} inputMode="numeric" />
            </Field>
          </FieldGrid>
        </FieldSection>
        </>
      )}

      {testing && <p className="text-base text-muted-foreground">{testing}</p>}

    </Modal>
  );
}
