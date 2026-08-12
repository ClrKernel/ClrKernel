import { describe, expect, it } from 'vitest';
import {
    buildDaxConnectDirective,
    buildSqlConnectDirective,
    nextUntitledNotebookName,
    quote,
} from '../src/directives';

/**
 * These strings are the contract with the kernel's directive parsers. The connection buttons build
 * them and the kernel parses them, and nothing else checks that the two agree.
 */
describe('quote', () => {
    it('leaves a simple value alone', () => {
        expect(quote('warehouse')).toBe('warehouse');
        expect(quote('sql01.corp.local')).toBe('sql01.corp.local');
    });

    it('wraps anything with whitespace, so the tokenizer keeps it whole', () => {
        expect(quote('My Workspace')).toBe('"My Workspace"');
        expect(quote('Sales Model')).toBe('"Sales Model"');
    });

    it('strips embedded quotes rather than emitting an unbalanced string', () => {
        expect(quote('say "hi"')).toBe('"say hi"');
    });
});

describe('buildSqlConnectDirective', () => {
    it('names the connection, server and auth mode', () => {
        expect(buildSqlConnectDirective({ name: 'wh', server: 'sql01', database: 'dw', auth: 'integrated' }))
            .toBe('#!sql-connect --name wh --server sql01 --database dw --auth integrated');
    });

    it('quotes values containing spaces', () => {
        const d = buildSqlConnectDirective({ name: 'my dw', server: 'sql 01', database: 'a b', auth: 'sql', user: 'svc acct' });
        expect(d).toContain('--name "my dw"');
        expect(d).toContain('--server "sql 01"');
        expect(d).toContain('--database "a b"');
        expect(d).toContain('--user "svc acct"');
    });

    it('omits an absent database rather than sending an empty flag', () => {
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated' }))
            .not.toContain('--database');
    });

    it('states encryption only when it is off, since on is the default', () => {
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated' })).not.toContain('--encrypt');
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated', encrypt: true })).not.toContain('--encrypt');
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated', encrypt: false })).toContain('--encrypt false');
    });

    it('adds --trust-cert only when asked', () => {
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated', trustCert: true })).toContain('--trust-cert');
        expect(buildSqlConnectDirective({ name: 'n', server: 's', auth: 'integrated' })).not.toContain('--trust-cert');
    });
});

describe('buildDaxConnectDirective', () => {
    it('builds a Fabric cube from workspace and model', () => {
        expect(buildDaxConnectDirective({ name: 'fcst', kind: 'fabric', workspace: 'DataWarehouse', model: 'Forecast' }))
            .toBe('#!dax-connect --name fcst --fabric --workspace DataWarehouse --model Forecast');
    });

    it('builds Azure Analysis Services with its own flag', () => {
        const d = buildDaxConnectDirective({ name: 'aas', kind: 'azure-as', server: 'asazure://westus.asazure.windows.net/s', database: 'M' });
        expect(d).toContain('--azure-as');
        expect(d).toContain('--server asazure://westus.asazure.windows.net/s');
    });

    it('builds an on-prem cube with no auth flag at all', () => {
        const d = buildDaxConnectDirective({ name: 'o', kind: 'on-prem', server: 'ssas01', database: 'M' });
        expect(d).toBe('#!dax-connect --name o --server ssas01 --database M');
        expect(d).not.toContain('--azure-as');
        expect(d).not.toContain('--integrated');
    });

    // The Entra token is the default and therefore carries NO flag. A build that treated "no flag"
    // as "nothing chosen" silently dropped every Fabric and Azure AS connection, so these two pin
    // that an absent --integrated still produces a complete, usable directive.
    it('produces a complete Fabric directive when Entra (the default) is chosen', () => {
        const d = buildDaxConnectDirective({ name: 'f', kind: 'fabric', workspace: 'W', model: 'M', integrated: false });
        expect(d).toContain('--fabric');
        expect(d).toContain('--workspace W');
        expect(d).not.toContain('--integrated');
    });

    it('adds --integrated for the Windows identity on both cloud kinds', () => {
        expect(buildDaxConnectDirective({ name: 'f', kind: 'fabric', workspace: 'W', model: 'M', integrated: true }))
            .toContain('--integrated');
        expect(buildDaxConnectDirective({ name: 'a', kind: 'azure-as', server: 's', database: 'M', integrated: true }))
            .toContain('--integrated');
    });

    it('never adds --integrated to an on-prem cube, which is already Integrated', () => {
        expect(buildDaxConnectDirective({ name: 'o', kind: 'on-prem', server: 's', database: 'M', integrated: true }))
            .not.toContain('--integrated');
    });

    it('quotes a workspace or model containing spaces', () => {
        const d = buildDaxConnectDirective({ name: 'f', kind: 'fabric', workspace: 'My WS', model: 'Sales Model' });
        expect(d).toContain('--workspace "My WS"');
        expect(d).toContain('--model "Sales Model"');
    });
});

describe('nextUntitledNotebookName', () => {
    it('starts at 1 and keeps the .nb.md double extension', () => {
        // A plain .md file is not matched by the notebook type's *.nb.md selector, which is the
        // whole reason this exists.
        expect(nextUntitledNotebookName([])).toBe('Untitled-1.nb.md');
    });

    it('skips names already open', () => {
        expect(nextUntitledNotebookName(['/Untitled-1.nb.md'])).toBe('Untitled-2.nb.md');
        expect(nextUntitledNotebookName(['/Untitled-1.nb.md', '/Untitled-2.nb.md'])).toBe('Untitled-3.nb.md');
    });

    it('ignores gaps rather than reusing a number in the middle', () => {
        expect(nextUntitledNotebookName(['/Untitled-2.nb.md'])).toBe('Untitled-1.nb.md');
    });

    it('compares by file name, not by the whole path', () => {
        expect(nextUntitledNotebookName(['/some/deep/folder/Untitled-1.nb.md'])).toBe('Untitled-2.nb.md');
    });

    it('is not confused by real notebooks that happen to be open', () => {
        expect(nextUntitledNotebookName(['/work/Sales.nb.md', '/work/notes.md'])).toBe('Untitled-1.nb.md');
    });
});
