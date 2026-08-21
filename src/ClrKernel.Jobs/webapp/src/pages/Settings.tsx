import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, type SettingField, type SettingsSection } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

function FieldValue({ field }: { field: SettingField }) {
  if (field.type === 'secret') {
    return <span className={field.isSet ? '' : 'muted'}>{field.isSet ? '(set)' : '(not set)'}</span>;
  }
  return <span>{String(field.value ?? '')}</span>;
}

/**
 * One section rendered from its schema. New server-side sections show up here with
 * zero UI work — that is the point of the registry.
 */
function Section({ section }: { section: SettingsSection }) {
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const editable = section.fields.filter((f) => f.webWritable);
  const dirty = Object.keys(edits).length > 0;

  async function save() {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.saveSettings(section.key, edits);
      setEdits({});
      setNotice(result.restartRequired ? 'Saved. Restart the server to apply.' : 'Saved.');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="settings-section">
      <h2>{section.title}</h2>
      {section.description && <p className="muted">{section.description}</p>}
      <ErrorBanner error={error} />
      {notice && <div className="banner banner-ok">{notice}</div>}

      {section.linkTo ? (
        <Link className="button" to={section.linkTo}>
          Open editor
        </Link>
      ) : (
        <table className="table">
          <tbody>
            {section.fields.map((field) => {
              // Pinned by CLI/env, or not web-writable at all: display only.
              const locked = !field.webWritable || (field.source !== 'default' && field.source !== 'settings.json');
              return (
                <tr key={field.name}>
                  <td className="settings-label">
                    {field.label}
                    {field.help && <div className="muted small">{field.help}</div>}
                  </td>
                  <td>
                    {locked ? (
                      <FieldValue field={field} />
                    ) : field.type === 'bool' ? (
                      <input
                        type="checkbox"
                        checked={(edits[field.name] as boolean | undefined) ?? Boolean(field.value)}
                        onChange={(e) => setEdits({ ...edits, [field.name]: e.target.checked })}
                      />
                    ) : (
                      <input
                        type={field.type === 'int' ? 'number' : 'text'}
                        value={String(edits[field.name] ?? field.value ?? '')}
                        onChange={(e) =>
                          setEdits({
                            ...edits,
                            [field.name]: field.type === 'int' ? Number(e.target.value) : e.target.value,
                          })
                        }
                      />
                    )}
                  </td>
                  <td className="muted small">
                    {field.source !== 'default' && <span className="chip chip-muted">{field.source}</span>}
                    {!field.webWritable && <span className="chip chip-muted">host-only</span>}
                    {field.restartRequired && field.webWritable && (
                      <span className="chip chip-muted">restart to apply</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      {editable.length > 0 && (
        <button className="button button-primary" onClick={save} disabled={busy || !dirty}>
          Save {section.title.toLowerCase()}
        </button>
      )}
    </section>
  );
}

export function Settings() {
  const { data, error } = usePolling(() => api.settings(), null);
  return (
    <div>
      <h1>Settings</h1>
      <ErrorBanner error={error} />
      <p className="muted">
        Values pinned by a flag or environment variable are shown locked — change those on the
        host. Security and execution settings are host-only by design.
      </p>
      {(data?.sections ?? []).map((section) => (
        <Section key={section.key} section={section} />
      ))}
    </div>
  );
}
