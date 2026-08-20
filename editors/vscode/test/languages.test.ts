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
