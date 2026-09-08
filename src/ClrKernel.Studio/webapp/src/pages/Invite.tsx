import { KeyRound } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { auth, passkeyBlocker, type InviteOffer, type SessionState } from '../auth';
import { AuthShell } from './SignIn';

/**
 * Redeeming an invite. Invalid, expired, revoked and already-used all say the
 * same thing on purpose — telling them apart is a way to learn which codes exist.
 *
 * <p>Nothing to fill in: the admin who issued this settled the name and the
 * handle, which is what lets a collision be refused on their form instead of
 * here, where somebody is holding a security key.</p>
 */
export function Invite({
  session,
  onSignedIn,
}: {
  session: SessionState | null;
  onSignedIn: () => void;
}) {
  const { code = '' } = useParams<{ code: string }>();
  const [offer, setOffer] = useState<InviteOffer | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    auth.invite(code).then(setOffer).catch(() => setOffer({ valid: false }));
  }, [code]);

  async function go(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await auth.acceptInvite(code);
      onSignedIn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (offer?.valid === false) {
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
      description="Register a passkey. That passkey is how you sign in from now on."
      session={session}
      error={error}
    >
      <form className="flex flex-col gap-3" onSubmit={go}>
        {offer?.valid && (
          <div className="rounded-md border bg-muted/40 px-3 py-2 text-base">
            You’re joining as <strong>{offer.displayName}</strong>
            {/* The handle, because it is the name on their branch and their folder
                in every project — worth seeing before it is theirs, and only an
                admin can change it afterwards. */}
            {offer.username && (
              <>
                {' '}
                <span className="text-muted-foreground">
                  (<code className="font-mono text-xs">{offer.username}</code>)
                </span>
              </>
            )}
            . Ask whoever invited you if that isn’t right.
          </div>
        )}
        <Button
          type="submit"
          disabled={busy || !offer?.valid || passkeyBlocker(session) != null}
        >
          <KeyRound className="size-4" aria-hidden="true" />
          {busy ? 'Waiting for your passkey…' : 'Create my account'}
        </Button>
      </form>
    </AuthShell>
  );
}
