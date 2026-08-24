import {
  startAuthentication,
  startRegistration,
  browserSupportsWebAuthn,
} from '@simplewebauthn/browser';

/** Server-wide roles. Names match the C# enum, which is what the API sends. */
export type Role = 'ServerAdmin' | 'ServerViewer' | 'ServerUser';

export interface SessionUser {
  id: string;
  displayName: string;
  role: Role;
}

export interface SessionState {
  authenticated: boolean;
  /** No accounts yet: the first person here claims the server. */
  needsSetup: boolean;
  /**
   * Whether the *server* saw TLS on this request. Diagnostics only — behind a
   * proxy that terminates TLS it is false on a perfectly good HTTPS origin, so
   * it must never gate the UI. `window.isSecureContext` is the browser's own
   * answer and is the one that matters.
   */
  secureContext: boolean;
  relyingPartyId: string;
  user: SessionUser | null;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const parsed = text ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new Error(parsed?.error ?? `${response.status} ${response.statusText}`);
  }
  return parsed as T;
}

export async function loadSession(): Promise<SessionState> {
  const response = await fetch('/api/auth/session');
  return (await response.json()) as SessionState;
}

/**
 * Why passkeys will not work here, or null when they will.
 *
 * Checked before the button rather than after: a browser that refuses WebAuthn
 * does it at the prompt, which from the outside looks like the machine ignoring
 * you. The two real causes are an old browser and an origin that is neither
 * HTTPS nor localhost — and the second is much the more likely.
 */
export function passkeyBlocker(_session: SessionState | null): string | null {
  if (!browserSupportsWebAuthn()) {
    return 'This browser does not support passkeys. Chrome, Edge, Safari and Firefox all do.';
  }
  // The browser already knows, and it is the only opinion that counts: it also
  // gets 127.0.0.1 and [::1] right, and it is not fooled by a TLS-terminating
  // proxy the way asking the server would be.
  if (!window.isSecureContext) {
    return (
      `Passkeys need a secure context. This page is ${location.origin}, which the browser ` +
      'treats as insecure, so it will refuse to create or use one. Put the server behind TLS ' +
      '(or reach it at localhost) and try again.'
    );
  }
  return null;
}

/** A registration ceremony, whichever door it came through. */
async function register(
  beginPath: string,
  completePath: string,
  body: Record<string, unknown>,
  passkeyName?: string,
): Promise<SessionUser> {
  const { ceremonyId, options } = await post<{ ceremonyId: string; options: unknown }>(
    beginPath,
    body,
  );
  // The browser owns the prompt from here; a cancelled or timed-out ceremony
  // throws, and its message is what the user is told.
  const response = await startRegistration({ optionsJSON: options as never });
  const result = await post<{ user: SessionUser }>(completePath, {
    ceremonyId,
    response,
    passkeyName,
  });
  return result.user;
}

export interface Passkey {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface ManagedUser {
  id: string;
  displayName: string;
  role: Role;
  disabled: boolean;
  createdAt: string;
  lastSeenAt: string | null;
  credentialCount: number;
  /** You cannot disable or remove yourself, so the UI needs to know which is you. */
  isYou: boolean;
}

export interface ManagedInvite {
  code: string;
  role: Role;
  label: string | null;
  createdAt: string;
  expiresAt: string;
  usedAt: string | null;
  revoked: boolean;
  status: 'open' | 'used' | 'revoked' | 'expired';
}

async function send<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const parsed = text ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new Error(parsed?.error ?? `${response.status} ${response.statusText}`);
  }
  return parsed as T;
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(path);
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

export const accounts = {
  passkeys: () => get<{ passkeys: Passkey[] }>('/api/auth/passkeys').then((r) => r.passkeys),
  removePasskey: (id: string) => send('DELETE', `/api/auth/passkeys/${encodeURIComponent(id)}`),
  rename: (displayName: string) => send('PUT', '/api/auth/profile', { displayName }),

  users: () => get<{ users: ManagedUser[] }>('/api/users').then((r) => r.users),
  setRole: (id: string, role: Role) => send('PUT', `/api/users/${id}/role`, { role }),
  setDisabled: (id: string, disabled: boolean) =>
    send('PUT', `/api/users/${id}/disabled`, { disabled }),
  removeUser: (id: string) => send('DELETE', `/api/users/${id}`),

  invites: () => get<{ invites: ManagedInvite[] }>('/api/invites').then((r) => r.invites),
  createInvite: (role: Role, label: string) =>
    send<{ code: string; expiresAt: string }>('POST', '/api/invites', { role, label }),
  revokeInvite: (code: string) => send('DELETE', `/api/invites/${encodeURIComponent(code)}`),
};

export const auth = {
  /** First run: creates the Server Admin and signs them in. */
  setup: (displayName: string) =>
    register('/api/auth/setup/begin', '/api/auth/setup/complete', { displayName }),

  /** Redeem an invite: creates an account at the invite's role and signs in. */
  acceptInvite: (code: string, displayName: string) =>
    register(
      `/api/auth/invite/${encodeURIComponent(code)}/begin`,
      `/api/auth/invite/${encodeURIComponent(code)}/complete`,
      { displayName },
    ),

  /** Add a device to the account already signed in. */
  addPasskey: (name: string) =>
    register('/api/auth/passkeys/begin', '/api/auth/passkeys/complete', {}, name),

  /**
   * Sign in. No username field: the credentials are discoverable, so the
   * authenticator offers what it holds and the server learns who it is from the
   * assertion it gets back.
   */
  signIn: async (): Promise<SessionUser> => {
    const { ceremonyId, options } = await post<{ ceremonyId: string; options: unknown }>(
      '/api/auth/signin/begin',
    );
    const response = await startAuthentication({ optionsJSON: options as never });
    const result = await post<{ user: SessionUser }>('/api/auth/signin/complete', {
      ceremonyId,
      response,
    });
    return result.user;
  },

  signOut: () => post('/api/auth/signout'),

  inviteIsValid: async (code: string): Promise<boolean> => {
    const response = await fetch(`/api/auth/invite/${encodeURIComponent(code)}`);
    return response.ok && ((await response.json()) as { valid: boolean }).valid;
  },
};
