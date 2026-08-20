import { describe, expect, it } from 'vitest';
import {
    composeConnectDirective,
    ConnectionProviderDescriptor,
    quote,
} from '../src/connectionDirective';
import { nextUntitledNotebookName } from '../src/directives';
import { DirectiveDefinition } from '../src/languages';

describe('quote', () => {
    it('leaves plain values alone', () => {
        expect(quote('warehouse')).toBe('warehouse');
        expect(quote('sql01.corp.local')).toBe('sql01.corp.local');
    });

    it('wraps values containing whitespace', () => {
        expect(quote('My Workspace')).toBe('"My Workspace"');
    });

    it('strips embedded quotes rather than escaping (the kernel tokenizer has no escapes)', () => {
        expect(quote('say "hi"')).toBe('"say hi"');
    });
});

// Fixtures mirroring the wire shape of clrkernel/connections/describe and the
// language descriptor's directive tables.
const sqlDirective: DirectiveDefinition = {
    selector: '#!sql-connect',
    parameters: [
        { name: '--name' }, { name: '--server' }, { name: '--database' }, { name: '--auth' },
        { name: '--user' }, { name: '--encrypt' },
        { name: '--trust-cert', kind: 'flag' },
        { name: '--secret' },
    ],
};

const sqlProvider: ConnectionProviderDescriptor = {
    type: 'SqlServer',
    displayName: 'SQL Server',
    connectSelector: '#!sql-connect',
    settings: [
        { name: 'name', required: true, directiveFlag: '--name' },
        { name: 'server', oneOfGroup: 'target', directiveFlag: '--server' },
        { name: 'connectionString', oneOfGroup: 'target', directiveFlag: '--connection-string' },
        { name: 'database', directiveFlag: '--database' },
        { name: 'auth', kind: 'enum', enumValues: ['sql', 'integrated', 'entra'], default: 'integrated', directiveFlag: '--auth' },
        { name: 'user', directiveFlag: '--user' },
        { name: 'password', kind: 'secretRef', directiveFlag: '--secret' },
        { name: 'encrypt', kind: 'bool', default: 'true', directiveFlag: '--encrypt' },
        { name: 'trustServerCertificate', kind: 'bool', default: 'false', directiveFlag: '--trust-cert' },
    ],
};

describe('composeConnectDirective', () => {
    it('states every given value, quotes whitespace, and skips the untouched', () => {
        const line = composeConnectDirective(sqlProvider, sqlDirective, {
            name: 'wh', server: 'sql server.local', database: 'dw', auth: 'sql', user: 'sa',
        });
        expect(line).toBe('#!sql-connect --name wh --server "sql server.local" --database dw --auth sql --user sa');
    });

    it('always states non-bool values, even at their default — the kernel ladder must not re-infer', () => {
        const line = composeConnectDirective(sqlProvider, sqlDirective, {
            name: 'wh', server: 's', auth: 'integrated', user: 'svc',
        });
        expect(line).toContain('--auth integrated');
    });

    it('omits bool defaults, emits a bare switch for flag-kind bools and a value otherwise', () => {
        const defaults = composeConnectDirective(sqlProvider, sqlDirective, {
            name: 'wh', server: 's', encrypt: 'true', trustServerCertificate: 'false',
        });
        expect(defaults).toBe('#!sql-connect --name wh --server s');

        const flipped = composeConnectDirective(sqlProvider, sqlDirective, {
            name: 'wh', server: 's', encrypt: 'false', trustServerCertificate: 'true',
        });
        expect(flipped).toBe('#!sql-connect --name wh --server s --encrypt false --trust-cert');
    });

    it('never emits the secret — it rides the RPC parameter, not the line', () => {
        const line = composeConnectDirective(sqlProvider, sqlDirective, {
            name: 'wh', server: 's', user: 'sa', password: 'hunter2',
        });
        expect(line).not.toContain('hunter2');
        expect(line).not.toContain('--secret');
    });

    it('maps a flagless enum to the matching directive switch (PSRemoting transport)', () => {
        const pwshDirective: DirectiveDefinition = {
            selector: '#!pwsh-connect',
            parameters: [
                { name: '--name' }, { name: '--host' },
                { name: '--ssh', kind: 'flag' },
                { name: '--winrm', kind: 'flag' },
            ],
        };
        const pwshProvider: ConnectionProviderDescriptor = {
            type: 'PSRemoting', displayName: 'PowerShell Remoting', connectSelector: '#!pwsh-connect',
            settings: [
                { name: 'name', directiveFlag: '--name' },
                { name: 'host', directiveFlag: '--host' },
                { name: 'transport', kind: 'enum', enumValues: ['ssh', 'winrm'], default: 'ssh' },
            ],
        };
        expect(composeConnectDirective(pwshProvider, pwshDirective, { name: 'w', host: 'h', transport: 'winrm' }))
            .toBe('#!pwsh-connect --name w --host h --winrm');
        expect(composeConnectDirective(pwshProvider, pwshDirective, { name: 'w', host: 'h', transport: 'ssh' }))
            .toBe('#!pwsh-connect --name w --host h --ssh');
    });

    it('composes the DAX fabric form from workspace and model', () => {
        const daxProvider: ConnectionProviderDescriptor = {
            type: 'AnalysisServices', displayName: 'Analysis Services', connectSelector: '#!dax-connect',
            settings: [
                { name: 'name', directiveFlag: '--name' },
                { name: 'workspace', oneOfGroup: 'target', directiveFlag: '--workspace' },
                { name: 'model', directiveFlag: '--model' },
            ],
        };
        expect(composeConnectDirective(daxProvider, undefined, {
            name: 'sales', workspace: 'Analytics WS', model: 'Sales Model',
        })).toBe('#!dax-connect --name sales --workspace "Analytics WS" --model "Sales Model"');
    });
});

describe('nextUntitledNotebookName', () => {
    it('numbers past whatever is open', () => {
        expect(nextUntitledNotebookName([])).toBe('Untitled-1.nb.md');
        expect(nextUntitledNotebookName(['/x/Untitled-1.nb.md', 'Untitled-2.nb.md'])).toBe('Untitled-3.nb.md');
    });
});
