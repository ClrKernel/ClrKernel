import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Plus, Trash2 } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { CheckboxField, Field, FieldRow, SelectField } from '@/components/ui/field';
import { Input } from '@/components/ui/input';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select';
import {
  api, type NotificationDelivery, type NotificationEventName, type NotificationRule,
} from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';
import { timeAgo } from '../ipynb';
import { useProjects } from '../projectContext';
import { DashboardTabs } from './Dashboard';

/** What each event means, said once, where somebody choosing between them can read it. */
const EVENTS: Record<NotificationEventName, string> = {
  JobFailed: 'a run finished in anything other than success',
  JobRecovered: 'a job succeeded whose previous run had not — the all-clear',
  RunTooSlow: 'a run took longer than the threshold',
  PromotedToProd: 'something reached production, including a deletion',
};

const ANY = '__any';

function RuleCard({
  rule, channels, onChange, onRemove, readOnly,
}: {
  rule: NotificationRule;
  channels: string[];
  onChange: (next: NotificationRule) => void;
  onRemove: () => void;
  readOnly: boolean;
}) {
  const { projects } = useProjects();
  const set = <K extends keyof NotificationRule>(key: K, value: NotificationRule[K]) =>
    onChange({ ...rule, [key]: value });

  return (
    <div className="max-w-[720px] rounded-2xl border border-border bg-card p-4">
      <div className="mb-3 flex items-start gap-2">
        <label className="flex-1">
          When
          <Select
            value={rule.event}
            disabled={readOnly}
            onValueChange={(event) => set('event', event as NotificationEventName)}
          >
            <SelectTrigger aria-label="Event"><SelectValue /></SelectTrigger>
            <SelectContent>
              {(Object.keys(EVENTS) as NotificationEventName[]).map((event) => (
                <SelectItem key={event} value={event}>{event}</SelectItem>
              ))}
            </SelectContent>
          </Select>
          <span className="block text-base text-muted-foreground">{EVENTS[rule.event]}</span>
        </label>
        {!readOnly && (
          <Button
            variant="outline"
            size="sm"
            className="mt-5"
            aria-label="Remove this rule"
            onClick={onRemove}
          >
            <Trash2 className="size-3.5" aria-hidden="true" />
          </Button>
        )}
      </div>

      <FieldRow>
        <SelectField
          label="In project"
          className="min-w-44 flex-1"
          value={rule.project || ANY}
          disabled={readOnly}
          onChange={(value) => set('project', value === ANY ? null : value)}
          options={[
            { value: ANY, label: 'every project' },
            ...projects.map((p) => ({ value: p.slug, label: p.name })),
          ]}
        />
        <SelectField
          label="On branch"
          className="min-w-40 flex-1"
          value={rule.environment || ANY}
          disabled={readOnly}
          onChange={(value) => set('environment', value === ANY ? null : value)}
          options={[
            { value: ANY, label: 'test and prod' },
            { value: 'test', label: 'test only' },
            { value: 'prod', label: 'prod only' },
          ]}
        />
        {rule.event === 'RunTooSlow' && (
          <Field label="Slower than (seconds)" className="w-40">
            <Input
              value={rule.afterSeconds ?? ''}
              disabled={readOnly}
              onChange={(e) => set('afterSeconds', Number(e.target.value) || null)}
            />
          </Field>
        )}
      </FieldRow>

      <fieldset className="fieldset">
        <legend>Send to</legend>
        {channels.length === 0 ? (
          <p className="text-base text-muted-foreground">
            No channels yet. <Link className="text-primary hover:underline" to="/channels">
              Add one
            </Link>{' '}— a rule with nowhere to send is a rule that looks configured and never
            arrives.
          </p>
        ) : (
          <div className="flex flex-wrap gap-3">
            {channels.map((name) => (
              <CheckboxField
                key={name}
                label={name}
                disabled={readOnly}
                checked={rule.to.includes(name)}
                onChange={(checked) => set('to', checked
                  ? [...rule.to, name]
                  : rule.to.filter((n) => n !== name))}
              />
            ))}
          </div>
        )}
      </fieldset>

      <CheckboxField
        className="mt-2"
        label="Enabled"
        disabled={readOnly}
        checked={rule.enabled}
        onChange={(enabled) => set('enabled', enabled)}
      />
    </div>
  );
}

/** The feed. Failures first-class, because they are the reason it exists. */
function Feed({ deliveries }: { deliveries: NotificationDelivery[] }) {
  if (deliveries.length === 0) {
    return <p className="text-base text-muted-foreground">Nothing has been sent yet.</p>;
  }
  return (
    <div className="table-box">
      <table className="table">
        <thead>
          <tr>
            <th>Sent</th>
            <th>Event</th>
            <th>About</th>
            <th>Channel</th>
            <th>Outcome</th>
          </tr>
        </thead>
        <tbody>
          {deliveries.map((d) => (
            <tr key={d.id} className={d.error ? 'row-failed' : undefined}>
              <td className="whitespace-nowrap text-muted-foreground">{timeAgo(d.sentAt)}</td>
              <td className="whitespace-nowrap">{d.event}</td>
              <td className="font-mono text-code text-muted-foreground">
                {d.runId ? (
                  <Link className="hover:underline" to={`/runs/${d.runId}`}>{d.subject}</Link>
                ) : d.subject}
              </td>
              <td className="whitespace-nowrap">{d.channel}</td>
              <td>
                {d.error
                  ? <span className="text-status-error">{d.error}</span>
                  : <Badge variant="secondary" className="font-normal">sent</Badge>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * When things get sent, and what actually went out.
 *
 * Against **Channels**, which is *where* — a destination and its credentials.
 * Keeping them apart is what stops one idea having two homes: a channel is
 * something you configure once, a rule is something each project decides.
 *
 * A job's own `notify:` block still works and is not shown here. Rules are
 * additive: somebody who wrote channels into a job did not ask for that to stop
 * meaning anything.
 */
export function Notifications() {
  const [rules, setRules] = useState<NotificationRule[] | null>(null);
  const [channels, setChannels] = useState<string[]>([]);
  const [problems, setProblems] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [failuresOnly, setFailuresOnly] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.notificationRules()
      .then((r) => {
        setRules(r.rules);
        setChannels(r.channels);
        setProblems(r.errors);
      })
      .catch((e: Error) => setError(e.message));
  }, []);

  const { data: feed } = usePolling<{ deliveries: NotificationDelivery[] }>(
    () => api.notifications(failuresOnly),
    10000,
    [failuresOnly],
  );

  async function save() {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const saved = await api.saveNotificationRules(rules ?? []);
      setRules(saved.rules);
      setProblems([]);
      setNotice('Saved.');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader title="Dashboard" />
      <DashboardTabs />
      <ErrorBanner error={error} />

      <section className="mb-6">
        <div className="mb-1.5 flex items-baseline justify-between gap-3">
          <h2 className="text-lg font-semibold">Rules</h2>
          <Link className="text-base text-primary hover:underline" to="/channels">
            Channels — where they go
          </Link>
        </div>
        {problems.length > 0 && (
          <ErrorBanner error={problems.join(' ')} />
        )}
        <div className="mb-3 flex flex-col gap-4">
          {(rules ?? []).map((rule, index) => (
            <RuleCard
              key={index}
              rule={rule}
              channels={channels}
              readOnly={false}
              onChange={(next) => setRules((was) =>
                (was ?? []).map((r, i) => (i === index ? next : r)))}
              onRemove={() => setRules((was) => (was ?? []).filter((_, i) => i !== index))}
            />
          ))}
          {rules != null && rules.length === 0 && (
            <p className="text-base text-muted-foreground">
              No rules yet. Nothing is sent except what a job names in its own{' '}
              <code className="font-mono text-code">notify:</code>.
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => setRules((was) => [
              ...(was ?? []),
              { event: 'JobFailed', to: [], enabled: true },
            ])}
          >
            <Plus className="size-3.5" aria-hidden="true" />
            Add a rule
          </Button>
          <Button size="sm" onClick={save} disabled={busy || rules == null}>
            {busy ? 'Saving…' : 'Save rules'}
          </Button>
          {notice && <span className="text-base text-muted-foreground">{notice}</span>}
        </div>
      </section>

      <section>
        <div className="mb-1.5 flex items-baseline justify-between gap-3">
          <h2 className="text-lg font-semibold">Delivered</h2>
          <CheckboxField
            label="only what did not arrive"
            checked={failuresOnly}
            onChange={setFailuresOnly}
          />
        </div>
        <Feed deliveries={feed?.deliveries ?? []} />
      </section>
    </div>
  );
}
