import { KeyRound } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { auth, passkeyBlocker, type SessionState } from '../auth';
import { AuthShell } from './SignIn';

/**
 * Redeeming an invite. Invalid, expired, revoked and already-used all say the
 * same thing on purpose — telling them apart is a way to learn which codes exist.
 */
export function Invite({
  session,
  onSignedIn,
}: {
  session: SessionState | null;
  onSignedIn: () => void;
}) {
  const { code = '' } = useParams<{ code: string }>();
  const [valid, setValid] = useState<boolean | null>(null);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    auth.inviteIsValid(code).then(setValid).catch(() => setValid(false));
  }, [code]);

  async function go(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await auth.acceptInvite(code, name.trim());
      onSignedIn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (valid === false) {
    return (
      <AuthShell title="This invite isn’t valid" session={session}>
        <p className="text-base text-muted-foreground">
          It may have been used already, expired, or been withdrawn. Ask whoever sent it for a new
          one.
        </p>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      title="Join this server"
      description="Pick a name and register a passkey. That passkey is how you sign in from now on."
      session={session}
      error={error}
    >
      <form className="flex flex-col gap-3" onSubmit={go}>
        <label className="flex flex-col gap-1 text-sm font-medium">
          Your name
          <Input
            value={name}
            autoFocus
            placeholder="Ada Lovelace"
            onChange={(e) => setName(e.target.value)}
          />
        </label>
        <Button
          type="submit"
          disabled={
            busy || valid !== true || name.trim().length === 0 || passkeyBlocker(session) != null
          }
        >
          <KeyRound className="size-4" aria-hidden="true" />
          {busy ? 'Waiting for your passkey…' : 'Create my account'}
        </Button>
      </form>
    </AuthShell>
  );
}
