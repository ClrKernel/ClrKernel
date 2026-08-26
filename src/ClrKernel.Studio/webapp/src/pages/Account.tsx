import { KeyRound, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { accounts, auth, passkeyBlocker, type ManagedUser, type Role } from '../auth';
import { ErrorBanner, usePolling } from '../components/common';
import { useSession } from '../sessionContext';
import { timeAgo } from '../ipynb';

const ROLES: { value: Role; label: string; hint: string }[] = [
  // Server User first: it is the one to reach for. The other two are server-wide
  // powers, and an account that can read every project makes a per-project grant
  // pointless — nothing is ever private to a project.
  {
    value: 'ServerUser',
    label: 'Server User',
    hint: 'Sees only the projects they are added to.',
  },
  { value: 'ServerViewer', label: 'Server Viewer', hint: 'Reads every project. For auditors.' },
  { value: 'ServerAdmin', label: 'Server Admin', hint: 'Everything, in every project.' },
];

function roleLabel(role: Role): string {
  return ROLES.find((r) => r.value === role)?.label ?? role;
}

/** Your own account: name, the devices you can sign in from, and the way out. */
export function AccountSection() {
  const session = useSession();
  const [name, setName] = useState(session?.user?.displayName ?? '');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const { data: passkeys, error: loadError, reload } = usePolling(() => accounts.passkeys(), null);
  const blocker = passkeyBlocker(session);

  async function run(work: () => Promise<unknown>, done: string) {
    setError(null);
    setBusy(true);
    try {
      await work();
      reload();
      toast.success(done);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="settings-section">
      <p className="mb-3 max-w-[78ch] text-base text-muted-foreground">
        You are signed in as <strong>{session?.user?.displayName}</strong> —{' '}
        {roleLabel(session?.user?.role ?? 'ServerUser')}. Roles are set by an admin.
      </p>
      <ErrorBanner error={error ?? loadError} />

      <h2 className="mb-1 text-lg font-semibold">Your name</h2>
      <form
        className="mb-5 flex max-w-md items-center gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          run(() => accounts.rename(name.trim()), 'Name updated.');
        }}
      >
        <Input value={name} aria-label="Display name" onChange={(e) => setName(e.target.value)} />
        <Button
          type="submit"
          variant="secondary"
          disabled={busy || !name.trim() || name.trim() === session?.user?.displayName}
        >
          Save
        </Button>
      </form>

      <h2 className="mb-1 text-lg font-semibold">Passkeys</h2>
      <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
        Add one per device — a laptop and a phone means losing either is an inconvenience rather
        than a lockout. There is no email in this system, so the last one cannot be removed.
      </p>
      <div className="table-box mb-3 max-w-[640px]">
        <table className="table">
          <thead>
            <tr>
              <th>Passkey</th>
              <th>Added</th>
              <th>Last used</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(passkeys ?? []).map((passkey) => (
              <tr key={passkey.id}>
                <td className="font-medium">{passkey.name}</td>
                <td className="text-muted-foreground">{timeAgo(passkey.createdAt)}</td>
                <td className="text-muted-foreground">
                  {passkey.lastUsedAt ? timeAgo(passkey.lastUsedAt) : 'never'}
                </td>
                <td className="text-right">
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label={`Remove ${passkey.name}`}
                    disabled={busy || (passkeys ?? []).length <= 1}
                    title={
                      (passkeys ?? []).length <= 1
                        ? 'This is your only passkey — add another first.'
                        : 'Remove this passkey'
                    }
                    onClick={() => run(() => accounts.removePasskey(passkey.id), 'Passkey removed.')}
                  >
                    <Trash2 className="size-3.5" aria-hidden="true" />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={busy || blocker != null}
          title={blocker ?? undefined}
          onClick={() =>
            run(() => auth.addPasskey(`Added ${new Date().toISOString().slice(0, 10)}`),
              'Passkey added.')
          }
        >
          <KeyRound className="size-3.5" aria-hidden="true" />
          Add a passkey
        </Button>
        <Button
          variant="outline"
          size="sm"
          onClick={async () => {
            await auth.signOut();
            location.href = '/signin';
          }}
        >
          Sign out
        </Button>
      </div>
    </section>
  );
}

/** Everyone with an account, and the invites that made them. Admins only. */
export function UsersSection() {
  const session = useSession();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [role, setRole] = useState<Role>('ServerUser');
  const [label, setLabel] = useState('');
  const { data: users, error: usersError, reload: reloadUsers } = usePolling(
    () => accounts.users(), null);
  const { data: invites, reload: reloadInvites } = usePolling(() => accounts.invites(), null);

  async function run(work: () => Promise<unknown>, done: string) {
    setError(null);
    setBusy(true);
    try {
      await work();
      reloadUsers();
      reloadInvites();
      toast.success(done);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function createInvite() {
    setError(null);
    setBusy(true);
    try {
      const { code } = await accounts.createInvite(role, label.trim());
      const url = `${location.origin}/invite/${code}`;
      setLabel('');
      reloadInvites();
      // There is no email in this system: the link is the whole delivery
      // mechanism, so it goes on the clipboard rather than into a table cell you
      // have to select by hand.
      await navigator.clipboard?.writeText(url).catch(() => undefined);
      toast.success('Invite created — link copied', {
        description: <code className="font-mono text-xs break-all">{url}</code>,
        duration: 20000,
        action: { label: 'Dismiss', onClick: () => toast.dismiss() },
      });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function describe(user: ManagedUser) {
    return user.isYou ? `${user.displayName} (you)` : user.displayName;
  }

  return (
    <section className="settings-section">
      <p className="mb-3 max-w-[78ch] text-base text-muted-foreground">
        Everyone who can reach this server. A Server Viewer can read notebooks, jobs and run
        history and nothing else — running a cell is code execution on this machine.
      </p>
      <ErrorBanner error={error ?? usersError} />

      <div className="table-box mb-6">
        <table className="table">
          <thead>
            <tr>
              <th>User</th>
              <th>Role</th>
              <th>Passkeys</th>
              <th>Last seen</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(users ?? []).map((user) => (
              <tr key={user.id} className={user.disabled ? 'opacity-60' : undefined}>
                <td className="font-medium">
                  {describe(user)}
                  {user.disabled && <span className="ml-2 text-xs text-muted-subtle">disabled</span>}
                </td>
                <td>
                  <Select
                    value={user.role}
                    onValueChange={(next) =>
                      run(() => accounts.setRole(user.id, next as Role), 'Role updated.')}
                  >
                    <SelectTrigger size="sm" aria-label={`Role for ${user.displayName}`}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {ROLES.map((option) => (
                        <SelectItem key={option.value} value={option.value}>
                          {option.label}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </td>
                <td className="text-muted-foreground">{user.credentialCount}</td>
                <td className="text-muted-foreground">
                  {user.lastSeenAt ? timeAgo(user.lastSeenAt) : 'never'}
                </td>
                <td className="text-right">
                  <span className="flex justify-end gap-1">
                    <Button
                      variant="outline"
                      size="xs"
                      disabled={busy || user.isYou}
                      title={user.isYou ? 'You cannot disable your own account.' : undefined}
                      onClick={() =>
                        run(() => accounts.setDisabled(user.id, !user.disabled),
                          user.disabled ? 'User enabled.' : 'User disabled.')}
                    >
                      {user.disabled ? 'Enable' : 'Disable'}
                    </Button>
                    <Button
                      variant="outline"
                      size="xs"
                      className="text-destructive hover:border-destructive hover:text-destructive"
                      disabled={busy || user.isYou}
                      onClick={() => {
                        if (confirm(`Remove ${user.displayName}? Their passkeys go with them.`)) {
                          run(() => accounts.removeUser(user.id), 'User removed.');
                        }
                      }}
                    >
                      Remove
                    </Button>
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h2 className="mb-1 text-lg font-semibold">Invites</h2>
      <p className="mb-2 max-w-[78ch] text-base text-muted-foreground">
        Single use, and they expire. Send the link however you like — this server has no email.
      </p>
      <div className="mb-3 flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1 text-sm font-medium">
          Role
          <Select value={role} onValueChange={(next) => setRole(next as Role)}>
            <SelectTrigger size="sm" aria-label="Invite role">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {ROLES.map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </label>
        <label className="flex flex-col gap-1 text-sm font-medium">
          Who it’s for <span className="font-normal text-muted-subtle">(optional)</span>
          <Input
            value={label}
            className="w-[220px]"
            placeholder="Bob on the data team"
            onChange={(e) => setLabel(e.target.value)}
          />
        </label>
        <Button size="sm" disabled={busy} onClick={createInvite}>
          Create invite
        </Button>
      </div>

      {(invites ?? []).length > 0 && (
        <div className="table-box max-w-[820px]">
          <table className="table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Role</th>
                <th>For</th>
                <th>Expires</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(invites ?? []).map((invite) => (
                <tr key={invite.code}>
                  <td>{invite.status}</td>
                  <td className="text-muted-foreground">{roleLabel(invite.role)}</td>
                  <td className="text-muted-foreground">{invite.label || '—'}</td>
                  <td className="text-muted-foreground">{timeAgo(invite.expiresAt)}</td>
                  <td className="text-right">
                    {invite.status === 'open' && (
                      <span className="flex justify-end gap-1">
                        <Button
                          variant="outline"
                          size="xs"
                          onClick={() =>
                            navigator.clipboard
                              ?.writeText(`${location.origin}/invite/${invite.code}`)
                              .then(() => toast.success('Link copied.'))}
                        >
                          Copy link
                        </Button>
                        <Button
                          variant="outline"
                          size="xs"
                          disabled={busy}
                          onClick={() =>
                            run(() => accounts.revokeInvite(invite.code), 'Invite withdrawn.')}
                        >
                          Withdraw
                        </Button>
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {session?.user == null && null}
    </section>
  );
}
