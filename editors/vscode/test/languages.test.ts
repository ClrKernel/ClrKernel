import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeEach, describe, expect, it } from 'vitest';
import {
    bundledLanguages,
    currentLanguages,
    languageForTag,
    selectorForTag,
    setLanguages,
    startsWithSelector,
    tagForCell,
} from '../src/languages';

/**
 * The extension's language registry: bundled defaults until the kernel's
 * handshake replaces them, and the pure helpers everything else (controller,
 * serializer) is built on.
 */
describe('language registry', () => {
    beforeEach(() => setLanguages(bundledLanguages));

    it('declares every language it ships that VS Code does not already know', () => {
        // The one thing about a cell language that cannot come from the kernel's
        // metadata. Behaviour is all descriptor-driven — fences, selectors,
        // completion, the picker — but *presentation* is static VSIX JSON: a
        // language VS Code has never heard of renders as plain text and shows its
        // raw id in the cell's language picker. Add a dialect to the list above
        // and forget package.json and nothing fails; it just looks broken.
        const manifest = JSON.parse(readFileSync(
            fileURLToPath(new URL('../package.json', import.meta.url)), 'utf8'));
        const declared = new Set<string>(
            (manifest.contributes.languages as { id: string }[]).map((l) => l.id));

        // The ones VS Code ships itself, which is why they were never declared.
        const builtIn = new Set(['sql', 'powershell', 'shellscript', 'markdown', 'json', 'yaml']);

        for (const language of bundledLanguages) {
            expect(
                declared.has(language.id) || builtIn.has(language.id),
                `${language.id}: neither declared in package.json nor a VS Code built-in`,
            ).toBe(true);
        }
    });

    it('ships the three SQL dialects, each claiming its own fence tags', () => {
        const dialects = bundledLanguages.filter((l) => l.category === 'SQL');
        expect(dialects.map((l) => l.id)).toEqual(['sql', 'oraclesql', 'ansisql']);

        // `sql` still means T-SQL. Every notebook already written says it, so the
        // dialects took new ids rather than this one taking a new meaning.
        expect(languageForTag('sql')?.id).toBe('sql');
        expect(languageForTag('tsql')?.id).toBe('sql');
        expect(languageForTag('oraclesql')?.id).toBe('oraclesql');
        expect(languageForTag('plsql')?.id).toBe('oraclesql');
        expect(languageForTag('ansisql')?.id).toBe('ansisql');
    });

    it('gives each dialect a selector that cannot be swallowed by another', () => {
        // Dispatch is longest-selector-first in the kernel, but only if the
        // selectors are distinct in the first place.
        const selectors = bundledLanguages.flatMap((l) => l.selectors);
        expect(new Set(selectors).size).toBe(selectors.length);
        expect(selectorForTag(languageForTag('oraclesql')!, 'oraclesql')).toBe('#!oraclesql');
        expect(startsWithSelector(languageForTag('oraclesql')!, '#!oraclesql\nSELECT 1 FROM DUAL')).toBe(true);
        expect(startsWithSelector(languageForTag('sql')!, '#!oraclesql\nSELECT 1')).toBe(false);
    });

    it('round-trips a dialect cell back to the tag it came from', () => {
        expect(tagForCell(languageForTag('oraclesql')!, 'SELECT 1 FROM DUAL')).toBe('oraclesql');
        expect(tagForCell(languageForTag('plsql')!, '#!oraclesql\nSELECT 1 FROM DUAL')).toBe('oraclesql');
        // ```tsql stays ```tsql, the way ```zsh stays ```zsh.
        expect(tagForCell(languageForTag('sql')!, '#!sql\nSELECT 1')).toBe('sql');
    });

    it('says which providers each dialect can run on', () => {
        const byId = (id: string) => bundledLanguages.find((l) => l.id === id);
        expect(byId('sql')?.supportedProviders).toEqual(['SqlServer', 'Odbc', 'Jdbc']);
        expect(byId('oraclesql')?.supportedProviders).toEqual(['Oracle', 'Odbc', 'Jdbc']);
        expect(byId('ansisql')?.supportedProviders).toEqual(['Odbc', 'Jdbc']);
    });

    it('keeps the bundled defaults when an old kernel serves nothing', () => {
        setLanguages(undefined);
        setLanguages([]);
        expect(currentLanguages()).toBe(bundledLanguages);
    });

    it('replaces the list wholesale when descriptors arrive — a plugin appears everywhere at once', () => {
        const toy = {
            id: 'toy', displayName: 'Toy', defaultSelector: '#!toy',
            selectors: ['#!toy'], languageTags: ['toy'],
        };
        setLanguages([...bundledLanguages, toy]);
        expect(currentLanguages().some((l) => l.id === 'toy')).toBe(true);
        expect(languageForTag('toy')?.id).toBe('toy');
    });

    it('maps language tags to descriptors, C# and unknown tags to none', () => {
        expect(languageForTag('tsql')?.id).toBe('sql');
        expect(languageForTag('PS1')?.id).toBe('powershell');
        expect(languageForTag('zsh')?.id).toBe('shellscript');
        expect(languageForTag('csharp')).toBeUndefined();
        expect(languageForTag('python')).toBeUndefined();
    });

    it('prefers a tag own selector, falling back to the default', () => {
        const shell = languageForTag('bash')!;
        expect(selectorForTag(shell, 'zsh')).toBe('#!zsh');
        expect(selectorForTag(shell, 'bash')).toBe('#!bash');
        const sql = languageForTag('sql')!;
        expect(selectorForTag(sql, 'tsql')).toBe('#!sql');
    });

    it('detects an existing selector as a whole token only', () => {
        const sql = languageForTag('sql')!;
        expect(startsWithSelector(sql, '#!sql\nselect 1')).toBe(true);
        expect(startsWithSelector(sql, '  #!sql-connect --name x')).toBe(true);
        expect(startsWithSelector(sql, '#!sqlfoo')).toBe(false);
        expect(startsWithSelector(sql, 'select 1')).toBe(false);
    });

    it('serializes cells back to a stable tag', () => {
        const shell = languageForTag('bash')!;
        expect(tagForCell(shell, 'echo hi')).toBe('bash');
        expect(tagForCell(shell, '#!zsh\necho hi')).toBe('zsh');
        const pwsh = languageForTag('pwsh')!;
        expect(tagForCell(pwsh, 'Get-Date')).toBe('powershell');
        const sql = languageForTag('sql')!;
        expect(tagForCell(sql, '#!sql-connect --name x')).toBe('sql');
    });
});
