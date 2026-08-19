import { useEffect, useState } from 'react';
import { api, type Channel } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

const WEBHOOK_TEMPLATE: Channel = {
  name: 'ops',
  type: 'webhook',
  url: 'https://example.com/hook',
  bearerSecretRef: 'ops-hook-token',
};

const EMAIL_TEMPLATE: Channel = {
  name: 'mail',
  type: 'email',
  host: 'smtp.example.com',
  port: 587,
  from: 'jobs@example.com',
  to: ['oncall@example.com'],
  user: 'jobs@example.com',
  passwordSecretRef: 'smtp-password',
};

/** Drops unset fields so the editor shows a channel, not a form with every blank. */
function compact(channel: Channel): Channel {
  return Object.fromEntries(
    Object.entries(channel).filter(([, value]) => value != null),
  ) as unknown as Channel;
}

function asDraft(channels: Channel[]): string {
  return JSON.stringify(channels.map(compact), null, 2);
}

function describe(channel: Channel): string {
  if (channel.type === 'webhook') {
    return channel.url ?? '(no url)';
  }
  if (channel.type === 'email') {
    return `${channel.host ?? '(no host)'}:${channel.port ?? 587} → ${(channel.to ?? []).join(', ')}`;
  }
  return channel.type;
}

export function Channels() {
  const { data, error, reload } = usePolling(() => api.channels(), null);
  const [draft, setDraft] = useState('');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setDraft(asDraft(data.channels));
    }
  }, [data]);

  async function save() {
    setSaveError(null);
    setNotice(null);
    let channels: Channel[];
    try {
      channels = JSON.parse(draft);
    } catch (e) {
      setSaveError(`Channels must be valid JSON: ${(e as Error).message}`);
      return;
    }
    if (!Array.isArray(channels)) {
      setSaveError('Expected a JSON array of channels.');
      return;
    }

    setBusy(true);
    try {
      await api.saveChannels(channels);
      setNotice('Saved to notifications.yaml.');
      reload();
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function test(name: string) {
    setSaveError(null);
    setNotice(null);
    setBusy(true);
    try {
      await api.testChannel(name);
      setNotice(`Sent a test notification to '${name}'.`);
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function addTemplate(template: Channel) {
    let current: Channel[] = [];
    try {
      current = JSON.parse(draft || '[]');
    } catch {
      // Keep whatever is in the box; the user can fix the JSON themselves.
      setSaveError('Fix the JSON before adding another channel.');
      return;
    }
    setDraft(asDraft([...current, template]));
  }

  const channels = data?.channels ?? [];
  const problems = data?.errors ?? [];

  return (
    <div>
      <h1>Notification channels</h1>
      <ErrorBanner error={error} />
      <ErrorBanner error={saveError} />
      {notice && <div className="banner banner-ok">{notice}</div>}
      {problems.length > 0 && (
        <div className="banner banner-warn">
          <ul>
            {problems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        </div>
      )}

      <p className="muted">
        Stored in <code>notifications.yaml</code> beside your notebooks. Passwords and tokens are
        never kept here — only a <em>reference</em> (<code>bearerSecretRef</code>,{' '}
        <code>passwordSecretRef</code>) that the server resolves from the OS credential store or a{' '}
        <code>CLRKERNEL_SECRET_*</code> variable.
      </p>

      {channels.length > 0 && (
        <table className="table">
          <thead>
            <tr>
              <th>Channel</th>
              <th>Type</th>
              <th>Target</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {channels.map((channel) => (
              <tr key={channel.name}>
                <td>{channel.name}</td>
                <td className="muted">{channel.type}</td>
                <td className="muted">{describe(channel)}</td>
                <td>
                  <button className="link-button" onClick={() => test(channel.name)} disabled={busy}>
                    send test
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>Edit</h2>
      <div className="form">
        <div className="row-gap">
          <button className="button" onClick={() => addTemplate(WEBHOOK_TEMPLATE)}>
            + webhook
          </button>
          <button className="button" onClick={() => addTemplate(EMAIL_TEMPLATE)}>
            + email
          </button>
        </div>
        <textarea rows={18} value={draft} onChange={(e) => setDraft(e.target.value)} />
        <div className="row-gap">
          <button className="button button-primary" onClick={save} disabled={busy}>
            Save channels
          </button>
          <button className="button" onClick={() => data && setDraft(asDraft(data.channels))}>
            Revert
          </button>
        </div>
      </div>
    </div>
  );
}
