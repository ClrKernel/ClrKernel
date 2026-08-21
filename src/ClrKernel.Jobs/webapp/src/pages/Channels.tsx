import { Plus } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { api, type Channel } from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';

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
      <PageHeader
        title="Notification channels"
        description={
          <>
            Stored in <code className="font-mono">notifications.yaml</code> beside your notebooks.
            Passwords and tokens are never kept here — only a <em>reference</em> (
            <code className="font-mono">bearerSecretRef</code>,{' '}
            <code className="font-mono">passwordSecretRef</code>) that the server resolves from the
            OS credential store or a <code className="font-mono">CLRKERNEL_SECRET_*</code> variable.
          </>
        }
      />
      <ErrorBanner error={error} />
      <ErrorBanner error={saveError} />
      {notice && (
        <Alert variant="success" className="mb-3">
          <AlertDescription className="text-status-success">{notice}</AlertDescription>
        </Alert>
      )}
      {problems.length > 0 && (
        <Alert variant="warning" className="mb-3">
          <AlertDescription>
            <ul className="list-disc pl-4">
              {problems.map((problem) => (
                <li key={problem}>{problem}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}

      {channels.length > 0 && (
        <div className="table-box">
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
                <td className="font-medium">{channel.name}</td>
                <td className="text-muted-foreground">{channel.type}</td>
                <td className="font-mono text-muted-foreground">{describe(channel)}</td>
                <td>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-6 px-2 text-sm"
                    onClick={() => test(channel.name)}
                    disabled={busy}
                  >
                    send test
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      )}

      <h2 className="mb-2 mt-5 text-lg font-semibold">Edit</h2>
      <div className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={() => addTemplate(WEBHOOK_TEMPLATE)}>
            <Plus className="size-3.5" aria-hidden="true" />
            webhook
          </Button>
          <Button variant="outline" size="sm" onClick={() => addTemplate(EMAIL_TEMPLATE)}>
            <Plus className="size-3.5" aria-hidden="true" />
            email
          </Button>
        </div>
        <textarea
          rows={18}
          value={draft}
          aria-label="Channels JSON"
          className="w-full rounded-md border border-border bg-card p-2 font-mono text-[13px] outline-none focus-visible:ring-2 focus-visible:ring-ring"
          onChange={(e) => setDraft(e.target.value)}
        />
        <div className="flex items-center gap-2">
          <Button size="sm" onClick={save} disabled={busy}>
            Save channels
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => data && setDraft(asDraft(data.channels))}
          >
            Revert
          </Button>
        </div>
      </div>
    </div>
  );
}
