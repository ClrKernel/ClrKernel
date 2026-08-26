import { describe, expect, it } from 'vitest';
import type { ApiConnectionProvider, ApiDirective } from './api';
import { composeConnectDirective, quote } from './connectionDirective';

// A stand-in for what a real provider descriptor looks like on the wire, with
// one setting of each shape that changes how the line is written.
const provider: ApiConnectionProvider = {
  type: 'mssql',
  displayName: 'SQL Server',
  connectSelector: '#!sql-connect',
  settings: [
    { name: 'name', kind: 'text', required: true, directiveFlag: '--name' },
    { name: 'server', kind: 'text', required: true, directiveFlag: '--server', oneOfGroup: 'target' },
    { name: 'connectionString', kind: 'text', directiveFlag: '--connection-string', oneOfGroup: 'target' },
    { name: 'password', kind: 'secretRef', directiveFlag: '--secret' },
    { name: 'trustCert', kind: 'bool', default: 'false', directiveFlag: '--trust-cert' },
    { name: 'encrypt', kind: 'bool', default: 'true', directiveFlag: '--encrypt' },
    { name: 'transport', kind: 'enum', enumValues: ['ssh', 'winrm'] },
    { name: 'timeout', kind: 'int', directiveFlag: '--timeout', runtimeOnly: true },
  ],
};

const directive: ApiDirective = {
  selector: '#!sql-connect',
  parameters: [
    { name: '--name', kind: 'value' },
    { name: '--server', kind: 'value' },
    { name: '--connection-string', kind: 'value' },
    { name: '--trust-cert', kind: 'flag' },
    { name: '--encrypt', kind: 'value' },
    { name: '--ssh', kind: 'flag' },
    { name: '--winrm', kind: 'flag' },
  ],
};

describe('composeConnectDirective', () => {
  it('writes the selector and the flags that carry values', () => {
    expect(composeConnectDirective(provider, directive, { name: 'prod', server: 'db01' }))
      .toBe('#!sql-connect --name prod --server db01');
  });

  it('writes a secret reference by name, which is what the field collects', () => {
    // Deliberately unlike the VS Code composer, which omits secretRef because the
    // extension stores the secret itself over RPC. A browser cannot do that, so
    // the field takes the *name* of an already-stored secret — a reference, which
    // is precisely what a notebook is allowed to carry.
    expect(composeConnectDirective(provider, directive, {
      name: 'prod', server: 'db01', password: 'prod-db-password',
    })).toBe('#!sql-connect --name prod --server db01 --secret prod-db-password');
  });

  it('quotes a value with whitespace so the kernel tokenizer keeps it whole', () => {
    expect(composeConnectDirective(provider, directive, { name: 'my db', server: 'db01' }))
      .toBe('#!sql-connect --name "my db" --server db01');
    expect(quote('a b')).toBe('"a b"');
    expect(quote('ab')).toBe('ab');
  });

  it('emits a bare switch for a bool the directive declares as a flag', () => {
    expect(composeConnectDirective(provider, directive, { name: 'p', server: 's', trustCert: 'true' }))
      .toBe('#!sql-connect --name p --server s --trust-cert');
  });

  it('omits a bool that equals its default — an absent switch is the default', () => {
    expect(composeConnectDirective(provider, directive, { name: 'p', server: 's', trustCert: 'false' }))
      .toBe('#!sql-connect --name p --server s');
  });

  it('writes a value-kind bool as an explicit true/false', () => {
    // --encrypt is declared 'value', and false is not its default, so it is stated.
    expect(composeConnectDirective(provider, directive, { name: 'p', server: 's', encrypt: 'false' }))
      .toBe('#!sql-connect --name p --server s --encrypt false');
  });

  it('turns a flagless enum into its own switch when the directive knows one', () => {
    expect(composeConnectDirective(provider, directive, { name: 'p', server: 's', transport: 'ssh' }))
      .toBe('#!sql-connect --name p --server s --ssh');
  });

  it('leaves out runtime-only settings, which never reach the notebook', () => {
    expect(composeConnectDirective(provider, directive, { name: 'p', server: 's', timeout: '30' }))
      .toBe('#!sql-connect --name p --server s');
  });

  it('reads kinds whatever their casing, because the two sources disagree', () => {
    // The kernel's RPC says "secretRef"/"bool"; the Jobs API re-serializes the
    // same descriptors with .NET's declared names. An exact comparison silently
    // never matched, which for a credential setting is not a cosmetic bug.
    const pascal: ApiConnectionProvider = {
      ...provider,
      settings: provider.settings.map((s) => ({
        ...s,
        kind: s.kind.charAt(0).toUpperCase() + s.kind.slice(1),
      })),
    };
    const pascalDirective: ApiDirective = {
      ...directive,
      parameters: directive.parameters!.map((p) => ({
        ...p,
        kind: (p.kind!.charAt(0).toUpperCase() + p.kind!.slice(1)) as never,
      })),
    };
    expect(composeConnectDirective(pascal, pascalDirective, {
      name: 'p', server: 's', trustCert: 'true', transport: 'ssh',
    })).toBe('#!sql-connect --name p --server s --trust-cert --ssh');
  });

  it('takes the other member of a one-of group', () => {
    expect(composeConnectDirective(provider, directive, {
      name: 'p', connectionString: 'Server=db;Database=x',
    })).toBe('#!sql-connect --name p --connection-string Server=db;Database=x');
  });
});
