import { useEffect, useMemo, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { api, type ApiConnection, type ApiConnectionProvider, type ApiConnectionSetting, type ApiLanguage } from '../api';
import { composeConnectDirective, isSecret, sameKind } from '../connectionDirective';
import { ErrorBanner } from './common';
import { Modal } from './Modal';

/**
 * The one connection wizard. It knows nothing about SQL, DAX or ODBC: the kernel
 * describes each connection type as a list of settings, and this renders that
 * description. A provider added by `#r`-ing a package into the session shows up
 * here with no change to the browser.
 *
 * **A password is never collected.** Settings that hold a credential take the
 * *name* of a secret — resolved on the machine that runs the notebook, from the
 * OS credential store or `CLRKERNEL_SECRET_*`. That is the invariant the whole
 * provider stack rests on, so the wizard states it rather than assuming it is
 * understood.
 */
export function ConnectionWizard({
  path, language, onInsert, onClose,
}: {
  path: string;
  language: ApiLanguage;
  onInsert: (directive: string) => void;
  onClose: () => void;
}) {
  const [providers, setProviders] = useState<ApiConnectionProvider[] | null>(null);
  const [saved, setSaved] = useState<ApiConnection[]>([]);
  // The saved list is the answer nearly every time. Defining one here is still
  // possible — a notebook that carries its own settings is legitimate — but it is
  // the thing you go looking for rather than the thing you land on.
  const [defining, setDefining] = useState(false);
  const [type, setType] = useState<string | null>(null);
  const [values, setValues] = useState<Record<string, string | boolean | undefined>>({});
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .connectionProviders(path, language.id)
      .then((result) => {
        setProviders(result.providers ?? []);
        setType(result.providers?.[0]?.type ?? null);
      })
      .catch((e) => setError((e as Error).message));
    api.connections()
      .then((result) => setSaved(result.connections))
      // Not an error worth showing: the wizard still works, it just cannot offer
      // the shortcut.
      .catch(() => setSaved([]));
  }, [path, language.id]);

  const provider = providers?.find((p) => p.type === type) ?? null;
  // Only the connections this language could actually open.
  const offered = saved.filter((c) => providers?.some((p) => p.type === c.type));

  /** `#!sql-connect --name warehouse --default` — the reference form. Nothing about
   *  the connection is copied into the notebook, which is the point: the settings
   *  stay in one place and the notebook names them. */
  function useSaved(connection: ApiConnection) {
    const owner = providers?.find((p) => p.type === connection.type);
    if (owner?.connectSelector) {
      onInsert(`${owner.connectSelector} --name ${connection.name} --default`);
    }
  }

  // Defaults come from the descriptor, so the preview is right before anything
  // is typed and "leave it alone" means the kernel's own default.
  useEffect(() => {
    if (!provider) {
      return;
    }
    const seeded: Record<string, string | boolean | undefined> = {};
    for (const setting of provider.settings) {
      if (setting.default != null) {
        seeded[setting.name] = setting.default;
      }
    }
    setValues(seeded);
  }, [provider]);

  const directive = useMemo(() => {
    if (!provider) {
      return '';
    }
    const definition = language.directives?.find((d) => d.selector === provider.connectSelector);
    return composeConnectDirective(provider, definition, values);
  }, [provider, language.directives, values]);

  const missing = provider
    ? provider.settings.filter((s) => s.required && !s.runtimeOnly && !filled(values[s.name]))
        .filter((s) => !satisfiedByGroup(s, provider, values))
    : [];

  return (
    <Modal title={`${language.displayName} connection`} onClose={onClose}>
      <ErrorBanner error={error} />

      {providers == null && !error && <p className="text-base text-muted-foreground">Asking the kernel what it can connect to…</p>}
      {providers?.length === 0 && (
        <p className="text-base text-muted-foreground">This kernel declares no connection types for {language.displayName}.</p>
      )}

      {!defining && offered.length > 0 && (
        <>
          <p className="text-base text-muted-foreground">
            Pick one the server already knows. The notebook names it and nothing else —
            the settings stay in one place, and a password never comes near the file.
          </p>
          <div className="wizard-saved">
            {offered.map((connection) => (
              <button key={connection.id} onClick={() => useSaved(connection)}>
                <span className="wizard-saved-name">{connection.name}</span>
                {connection.scope === 'private' ? (
                  <Badge variant="outline" className="font-normal">
                    only you
                  </Badge>
                ) : (
                  <Badge variant="outline" className="font-normal">shared</Badge>
                )}
                <span className="wizard-saved-detail">
                  {connection.settings.server ?? connection.settings.connectionString ?? connection.type}
                </span>
              </button>
            ))}
          </div>
          {offered.some((c) => c.scope === 'private') && (
            <p className="text-base text-muted-foreground">
              A connection marked <em>only you</em> resolves for nobody else and for no
              scheduled run, and promotion refuses a notebook that names one.
            </p>
          )}
          <Button variant="outline" size="sm" onClick={() => setDefining(true)}>
            Define one in this notebook instead
          </Button>
        </>
      )}

      {!defining && offered.length === 0 && providers != null && providers.length > 0 && (
        <p className="text-base text-muted-foreground">
          No saved connection this notebook could use. Add one in Connections to name it from
          any notebook, or define one here.
        </p>
      )}

      {(defining || offered.length === 0) && providers && providers.length > 1 && (
        <label className="form-field">
          <span>Connection type</span>
          <select value={type ?? ''} onChange={(e) => setType(e.target.value)}>
            {providers.map((p) => (
              <option key={p.type} value={p.type}>
                {p.displayName}
              </option>
            ))}
          </select>
        </label>
      )}

      {provider && (defining || offered.length === 0) && (
        <>
          {provider.description && <p className="text-base text-muted-foreground">{provider.description}</p>}
          <div className="wizard-fields">
            {provider.settings
              .filter((setting) => !setting.runtimeOnly)
              .map((setting) => (
                <Field
                  key={setting.name}
                  setting={setting}
                  value={values[setting.name]}
                  onChange={(v) => setValues((current) => ({ ...current, [setting.name]: v }))}
                />
              ))}
          </div>

          <p className="text-base text-muted-foreground">
            This is what will be inserted. Nothing secret appears in it — a credential setting
            takes the <em>name</em> of a secret, resolved where the notebook runs.
          </p>
          <pre className="output-text wizard-preview">{directive}</pre>

          {missing.length > 0 && (
            <p className="text-base text-muted-foreground">
              Still needed: {missing.map((s) => s.displayName ?? s.name).join(', ')}
            </p>
          )}

          <div className="flex items-center gap-2">
            <Button
              size="sm"
              disabled={missing.length > 0}
              onClick={() => onInsert(directive)}
            >
              Insert as a new cell
            </Button>
            <Button variant="outline" size="sm" onClick={onClose}>
              Cancel
            </Button>
          </div>
        </>
      )}
    </Modal>
  );
}

export function filled(value: string | boolean | undefined): boolean {
  return typeof value === 'boolean' ? true : (value ?? '').trim().length > 0;
}

/** A setting in a one-of group is satisfied when any member of the group is set —
 *  "server or connection string", not both. */
export function satisfiedByGroup(
  setting: ApiConnectionSetting,
  provider: ApiConnectionProvider,
  values: Record<string, string | boolean | undefined>,
): boolean {
  if (!setting.oneOfGroup) {
    return false;
  }
  return provider.settings.some(
    (other) => other.oneOfGroup === setting.oneOfGroup && filled(values[other.name]),
  );
}

/**
 * One descriptor-declared setting as an input. Exported because the Connections
 * area renders the same descriptors — a provider describes its settings once and
 * both the notebook wizard and the saved-connection form follow it.
 */
export function Field({
  setting, value, onChange,
}: {
  setting: ApiConnectionSetting;
  value: string | boolean | undefined;
  onChange: (value: string | boolean | undefined) => void;
}) {
  const label = setting.displayName ?? setting.name;

  if (sameKind(setting.kind, 'bool')) {
    return (
      <label className="form-field checkbox">
        <input
          type="checkbox"
          checked={String(value ?? setting.default ?? 'false') === 'true'}
          onChange={(e) => onChange(String(e.target.checked))}
        />
        <span>
          {label}
          {setting.description && <em className="text-base text-muted-foreground"> — {setting.description}</em>}
        </span>
      </label>
    );
  }

  return (
    <label className="form-field">
      <span>
        {label}
        {setting.required && <span className="wizard-required"> *</span>}
        {isSecret(setting.kind) && <Badge variant="outline" className="font-normal">secret name</Badge>}
      </span>
      {setting.enumValues?.length ? (
        <select value={String(value ?? '')} onChange={(e) => onChange(e.target.value)}>
          <option value="">(kernel default)</option>
          {setting.enumValues.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>
      ) : (
        <input
          type="text"
          value={String(value ?? '')}
          onChange={(e) => onChange(e.target.value)}
          placeholder={
            isSecret(setting.kind)
              ? 'name of a stored secret — not the password itself'
              : (setting.default ?? '')
          }
        />
      )}
      {setting.description && <em className="text-base text-muted-foreground">{setting.description}</em>}
    </label>
  );
}
