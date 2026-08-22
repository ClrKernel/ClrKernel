import { useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { api, type SettingField, type SettingsSection } from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';
import { TabNav } from '../components/TabNav';
import { useCanWrite } from '../sessionContext';
import { AccountSection, UsersSection } from './Account';

function FieldValue({ field }: { field: SettingField }) {
  if (field.type === 'secret') {
    return (
      <span className={field.isSet ? '' : 'text-muted-foreground'}>
        {field.isSet ? '(set)' : '(not set)'}
      </span>
    );
  }
  return <span>{String(field.value ?? '')}</span>;
}

/**
 * One section rendered from its schema. New server-side sections show up here with
 * zero UI work — that is the point of the registry.
 */
function Section({ section }: { section: SettingsSection }) {
  const canWrite = useCanWrite();
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
      {/* No heading: the active tab already says which section this is, and a
          title that repeats the tab an inch above it is just noise. */}
      {section.description && (
        <p className="mb-3 max-w-[78ch] text-base text-muted-foreground">{section.description}</p>
      )}
      <ErrorBanner error={error} />
      {notice && (
        <Alert variant="success" className="mb-2">
          <AlertDescription className="text-status-success">{notice}</AlertDescription>
        </Alert>
      )}

      {section.linkTo ? (
        <Button asChild variant="outline" size="sm">
          <Link to={section.linkTo}>Open editor</Link>
        </Button>
      ) : (
        <div className="table-box">
        <table className="table">
          <tbody>
            {section.fields.map((field) => {
              // Pinned by CLI/env, or not web-writable at all: display only.
              const locked = !canWrite || !field.webWritable
                || (field.source !== 'default' && field.source !== 'settings.json');
              return (
                <tr key={field.name}>
                  <td className="settings-label">
                    {field.label}
                    {field.help && (
                      <div className="mt-0.5 text-base text-muted-foreground">{field.help}</div>
                    )}
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
                      <Input
                        type={field.type === 'int' ? 'number' : 'text'}
                        className="h-8 max-w-sm"
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
                  <td>
                    <span className="flex flex-wrap gap-1">
                      {field.source !== 'default' && (
                        <Badge variant="outline" className="font-normal">
                          {field.source}
                        </Badge>
                      )}
                      {!field.webWritable && (
                        <Badge variant="outline" className="font-normal">
                          host-only
                        </Badge>
                      )}
                      {field.restartRequired && field.webWritable && (
                        <Badge variant="outline" className="font-normal">
                          restart to apply
                        </Badge>
                      )}
                    </span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        </div>
      )}

      {editable.length > 0 && canWrite && (
        <Button className="mt-3" size="sm" onClick={save} disabled={busy || !dirty}>
          Save {section.title.toLowerCase()}
        </Button>
      )}
    </section>
  );
}

/**
 * One section per tab, at its own URL.
 *
 * The tab list is built from whatever the server's settings registry returns
 * rather than from a hard-coded list, so a new section on the server still shows
 * up here with no UI work — the property the registry exists for. The URL
 * segment is the section's own key.
 */
export function Settings() {
  const { section: slug } = useParams<{ section: string }>();
  const { data, error } = usePolling(() => api.settings(), null);
  const isAdmin = useCanWrite();
  const sections = data?.sections ?? [];

  // Your account first — it is about you, and it is the one every role has. The
  // server's own sections follow, and user management is last and admin-only.
  const tabs = [
    { to: '/settings/account', label: 'Your account' },
    ...sections.map((s) => ({ to: `/settings/${s.key}`, label: s.title })),
    ...(isAdmin ? [{ to: '/settings/users', label: 'Users' }] : []),
  ];
  const current = sections.find((s) => s.key === slug);
  const client = slug === 'account' || (slug === 'users' && isAdmin);

  // Only redirect once the sections have actually arrived: bouncing to the
  // first tab while the list is still empty would send you to /settings/
  // undefined on a slow connection.
  if (slug == null) {
    return <Navigate to="/settings/account" replace />;
  }

  return (
    <div>
      <PageHeader
        title="Settings"
        description="Values pinned by a flag or environment variable are shown locked — change those on the host. Security and execution settings are host-only by design."
      />
      <ErrorBanner error={error} />

      <TabNav items={tabs} label="Settings sections" className="mb-5" />

      {slug === 'account' ? (
        <AccountSection />
      ) : slug === 'users' && isAdmin ? (
        <UsersSection />
      ) : current ? (
        <Section section={current} />
      ) : (
        sections.length > 0 && !client && (
          <p className="text-base text-muted-foreground">
            No settings section called “{slug}”.{' '}
            <Link className="text-primary hover:underline" to={tabs[0].to}>
              {tabs[0].label}
            </Link>
          </p>
        )
      )}
    </div>
  );
}
