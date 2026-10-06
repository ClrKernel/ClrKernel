import { BookOpenCheck, KeyRound, MonitorSmartphone } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Alert, AlertDescription } from '@/components/ui/alert';
import {
  authErrorFromUrl, passkeyBlocker, windowsSignInUrl, type SessionState, type WindowsMode,
} from '../auth';

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
  // With Windows sign-in on offer, a page that cannot do passkeys is still a page
  // you can sign in from — so the warning moves to the passkey button's tooltip
  // rather than sitting over a button that works.
  const blocker = session?.windowsSignIn ? null : passkeyBlocker(session);
  // A Windows sign-in comes back here by redirect, carrying its refusal.
  const shown = error ?? authErrorFromUrl();
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
          {shown && (
            <Alert variant="destructive" className="mt-4">
              <AlertDescription className="text-destructive">{shown}</AlertDescription>
            </Alert>
          )}

          <div className="mt-4">{children}</div>
        </div>
      </div>
    </div>
  );
}

/**
 * "Sign in with Windows": a link, not a button with a handler, because it has to
 * be a navigation — the browser only answers the server's Windows challenge for a
 * page it is loading. Rendered only where the server offers it.
 */
export function WindowsButton({
  session, mode, code, label, variant = 'outline',
}: {
  session: SessionState | null;
  mode: WindowsMode;
  code?: string;
  label: string;
  variant?: 'default' | 'outline';
}) {
  if (!session?.windowsSignIn) {
    return null;
  }
  return (
    <Button className="w-full" variant={variant} asChild>
      <a href={windowsSignInUrl(mode, code)} className="hover:no-underline">
        <MonitorSmartphone className="size-4" aria-hidden="true" />
        {label}
      </a>
    </Button>
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
      description={session?.windowsSignIn
        ? 'With your Windows account, or a passkey you registered on this device.'
        : 'Use the passkey you registered on this device.'}
      session={session}
      error={error}
    >
      <div className="flex flex-col gap-2">
        <WindowsButton session={session} mode="signin" label="Sign in with Windows" variant="default" />
        <Button
          className="w-full"
          variant={session?.windowsSignIn ? 'outline' : 'default'}
          onClick={go}
          disabled={busy || passkeyBlocker(session) != null}
          title={passkeyBlocker(session) ?? undefined}
        >
          <KeyRound className="size-4" aria-hidden="true" />
          {busy ? 'Waiting for your passkey…' : 'Sign in with a passkey'}
        </Button>
      </div>
      <p className="mt-3 text-sm text-muted-subtle">
        {session?.windowsSignIn
          ? 'No account? Sign in with Windows — your admin may have set this server up to let you in — or ask them for an invite.'
          : 'No account? This server is invite-only — ask an admin for a link.'}
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
          Setup only answers a browser on the server itself. On that machine, open{' '}
          <code className="font-mono">http://localhost:5000</code> (or whatever port it listens
          on). A container’s published port does not count — the request arrives from the docker
          bridge — so there, get in with an invite instead:
        </p>
        <pre className="mt-3 overflow-x-auto rounded-lg border border-border bg-muted px-3 py-2 font-mono text-sm">
          docker exec &lt;container&gt; clrkernel-studio new-admin-invite
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
      description={session?.windowsSignIn
        ? 'Nobody has claimed this server yet. Sign in with Windows, or register a passkey, and you become its Server Admin.'
        : 'Nobody has claimed this server yet. Register a passkey and you become its Server Admin.'}
      session={session}
      error={error}
    >
      {session?.windowsSignIn && (
        <div className="mb-4 flex flex-col gap-2">
          <WindowsButton session={session} mode="setup" label="Use my Windows account" variant="default" />
          <p className="text-center text-sm text-muted-subtle">or register a passkey</p>
        </div>
      )}
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
          variant={session?.windowsSignIn ? 'outline' : 'default'}
          disabled={busy || name.trim().length === 0 || passkeyBlocker(session) != null}
          title={passkeyBlocker(session) ?? undefined}
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
