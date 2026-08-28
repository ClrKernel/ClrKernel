import { BookOpenCheck, KeyRound } from 'lucide-react';
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
          {/* The same mark as the rail and the browser tab. It was a `>_` prompt,
              which the rename left behind on the one page you meet before any
              of the others. */}
          <span
            aria-hidden="true"
            className="flex size-[28px] items-center justify-center rounded-lg bg-primary text-primary-foreground"
          >
            <BookOpenCheck className="size-[17px]" />
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

  // The server refuses setup from anywhere but itself, and a container's
  // published port arrives from the docker bridge — so this is the normal path
  // for `docker run -p`, not an edge case.
  if (session != null && !session.canSetUp) {
    return (
      <AuthShell
        title="Set up this server"
        description="Nobody has claimed this server yet."
        session={session}
      >
        <p className="text-sm text-muted-foreground">
          Setup only answers a browser on the server itself, and a container’s published port does
          not count — the request arrives from the docker bridge. Get in with an invite instead:
        </p>
        <pre className="mt-3 overflow-x-auto rounded-lg border border-border bg-muted px-3 py-2 font-mono text-sm">
          docker exec &lt;container&gt; /app/studio/ClrKernel.Studio new-admin-invite
        </pre>
        <p className="mt-3 text-sm text-muted-subtle">
          It prints a single-use link. Open the <code className="font-mono">/invite/&lt;code&gt;</code>{' '}
          path on this address — the printed host and port are the server’s own, which is not
          where you reached it if the port was published as something else.
        </p>
      </AuthShell>
    );
  }

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
