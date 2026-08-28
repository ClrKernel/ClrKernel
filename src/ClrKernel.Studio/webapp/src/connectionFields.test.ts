import { describe, expect, it } from 'vitest';
import type { ApiConnectionProvider } from './api';
import { groupsOf, unmet } from './connectionFields';

const sqlServer: ApiConnectionProvider = {
  type: 'SqlServer',
  displayName: 'SQL Server',
  description: null,
  queryable: true,
  connectSelector: '#!sql-connect',
  settings: [
    { name: 'server', displayName: 'Server', kind: 'text', oneOfGroup: 'target' },
    { name: 'connectionString', displayName: 'Connection string', kind: 'text', oneOfGroup: 'target' },
    { name: 'database', displayName: 'Database', kind: 'text' },
    { name: 'user', displayName: 'User', kind: 'text', required: true },
  ],
} as unknown as ApiConnectionProvider;

describe('unmet', () => {
  it('asks for a target when neither alternative is given', () => {
    expect(unmet(sqlServer, { user: 'sa' })).toEqual(['Server or Connection string']);
  });

  it('is satisfied by either member, and never by both being demanded', () => {
    expect(unmet(sqlServer, { user: 'sa', server: 'dw' })).toEqual([]);
    expect(unmet(sqlServer, { user: 'sa', connectionString: 'Server=dw' })).toEqual([]);
  });

  it('does not name a group member twice when it is required in its own right', () => {
    const required = {
      ...sqlServer,
      settings: sqlServer.settings.map(
        (s) => (s.name === 'server' ? { ...s, required: true } : s)),
    } as ApiConnectionProvider;
    expect(unmet(required, { user: 'sa' })).toEqual(['Server or Connection string']);
  });

  it('asks for the user when the chosen auth signs in with a name', () => {
    const withAuth = {
      ...sqlServer,
      settings: [
        ...sqlServer.settings.filter((s) => s.name !== 'user'),
        { name: 'auth', displayName: 'Authentication', kind: 'enum',
          enumValues: ['sql', 'integrated'], credentialValues: ['sql'], default: 'integrated' },
        { name: 'user', displayName: 'User', kind: 'text' },
      ],
    } as unknown as ApiConnectionProvider;

    // The driver's own answer to this is "Login failed for user ''".
    expect(unmet(withAuth, { server: 'dw', auth: 'sql' })).toEqual(['User']);
    expect(unmet(withAuth, { server: 'dw', auth: 'sql', user: 'sa' })).toEqual([]);
    // Integrated needs no name at all — and it is the default, so an untouched
    // form must not be nagged about a field it does not use.
    expect(unmet(withAuth, { server: 'dw', auth: 'integrated' })).toEqual([]);
    expect(unmet(withAuth, { server: 'dw' })).toEqual([]);
  });

  it('reports a plain required setting', () => {
    expect(unmet(sqlServer, { server: 'dw' })).toEqual(['User']);
    expect(unmet(sqlServer, { server: 'dw', user: '   ' })).toEqual(['User']);
  });

  it('has nothing to say about a provider that was never chosen', () => {
    expect(unmet(null, {})).toEqual(['a connection type']);
  });

  it('lists each group once', () => {
    expect(groupsOf(sqlServer)).toEqual(['target']);
  });
});
