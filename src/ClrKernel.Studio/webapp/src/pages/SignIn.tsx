import { KeyRound } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { passkeyBlocker, type SessionState } from '../auth';

/**
 * The shell every signed-out page shares: a centred card on the app's canvas,
 * with the product mark, so being signed out still looks like this application
 * rather than like an error.
 */
export function AuthShell({
  title,
  description,
  session,
  error,
  children,
}: {
  title: string;
  description?: ReactNode;
  session: SessionState | null;
  error?: string | null;
  children: ReactNode;
}) {
  const blocker = passkeyBlocker(session);
  return (
    <div className="flex min-h-screen items-center justify-center bg-background px-6 py-12">
      <div className="w-full max-w-[420px]">
        <div className="mb-5 flex items-center gap-2.5">
          <span
            aria-hidden="true"
            className="flex size-[28px] items-center justify-center rounded-lg bg-primary font-mono text-[14px] font-semibold text-primary-foreground"
          >
            &gt;_
          </span>
          <span className="font-semibold">ClrKernel Studio</span>
        </div>

        <div className="rounded-2xl border border-border bg-card px-6 py-5">
          <h1 className="text-xl font-bold tracking-tight">{title}</h1>
          {description && (
            <p className="mt-1 text-base text-muted-foreground">{description}</p>
          )}

          {blocker && (
            <Alert variant="warning" className="mt-4">
              <AlertDescription>{blocker}</AlertDescription>
            </Alert>
          )}
          {error && (
            <Alert variant="destructive" className="mt-4">
              <AlertDescription className="text-destructive">{error}</AlertDescription>
            </Alert>
          )}

          <div className="mt-4">{children}</div>
        </div>
      </div>
    </div>
  );
}

/**
 * One button. There is no username field because the passkey is discoverable —
 * the authenticator knows which account it holds, so asking you to type it would
 * be asking for something the browser already has.
 */
export function SignIn({ session, onSignedIn }: { session: SessionState | null; onSignedIn: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function go() {
    setError(null);
    setBusy(true);
    try {
      const { auth } = await import('../auth');
      await auth.signIn();
      onSignedIn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell
      title="Sign in"
      description="Use the passkey you registered on this device."
      session={session}
      error={error}
    >
      <Button className="w-full" onClick={go} disabled={busy || passkeyBlocker(session) != null}>
        <KeyRound className="size-4" aria-hidden="true" />
        {busy ? 'Waiting for your passkey…' : 'Sign in with a passkey'}
      </Button>
      <p className="mt-3 text-sm text-muted-subtle">
        No account? This server is invite-only — ask an admin for a link.
      </p>
    </AuthShell>
  );
}

/**
 * First run. Whoever completes this becomes the Server Admin, which is why the
 * server only accepts it from the machine it is running on.
 */
export function Setup({ session, onSignedIn }: { session: SessionState | null; onSignedIn: () => void }) {
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function go(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const { auth } = await import('../auth');
      await auth.setup(name.trim());
      onSignedIn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell
      title="Set up this server"
      description="Nobody has claimed this server yet. Register a passkey and you become its Server Admin."
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
          disabled={busy || name.trim().length === 0 || passkeyBlocker(session) != null}
        >
          <KeyRound className="size-4" aria-hidden="true" />
          {busy ? 'Waiting for your passkey…' : 'Create the admin account'}
        </Button>
      </form>
      {session?.relyingPartyId === 'localhost' && (
        <p className="mt-3 text-sm text-muted-subtle">
          This server’s passkeys are bound to <code className="font-mono">localhost</code>. A
          passkey cannot move between domains, so anything you register now stops working the day
          the server answers to a real hostname — set <code className="font-mono">--rp-id</code>{' '}
          first if this is more than a look around.
        </p>
      )}
    </AuthShell>
  );
}
