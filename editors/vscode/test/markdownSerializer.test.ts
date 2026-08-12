import { describe, expect, it } from 'vitest';
import { NotebookCellData, NotebookCellKind, NotebookData } from '../test/vscode-stub';
import { MarkdownNotebookSerializer } from '../src/markdownSerializer';

/**
 * `.nb.md` files are the notebook format, and the same files are run headlessly by the kernel's
 * `#!import` and in CI. A fence tag that maps to the wrong languageId — or a languageId that
 * serializes back to the wrong fence — silently changes which cell language executes.
 *
 * CLAUDE.md calls this map one of the places that must stay in step when a cell language is added.
 */
const serializer = new MarkdownNotebookSerializer();

const read = (md: string) => serializer.deserializeNotebook(new TextEncoder().encode(md)) as unknown as NotebookData;
const write = (cells: NotebookCellData[]) =>
    new TextDecoder().decode(serializer.serializeNotebook({ cells } as never));
const code = (value: string, languageId: string) => new NotebookCellData(NotebookCellKind.Code, value, languageId);

describe('fence tag -> cell language', () => {
    const cases: Array<[string, string]> = [
        ['csharp', 'csharp-script'],
        ['c#', 'csharp-script'],
        ['cs', 'csharp-script'],
        ['http', 'http'],
        ['mermaid', 'mermaid'],
        ['powershell', 'powershell'],
        ['pwsh', 'powershell'],
        ['ps1', 'powershell'],
        ['sql', 'sql'],
        ['tsql', 'sql'],
        ['dax', 'dax'],
    ];

    it.each(cases)('```%s becomes a %s cell', (tag, languageId) => {
        const cells = read('```' + tag + '\nBODY\n```').cells;
        expect(cells).toHaveLength(1);
        expect(cells[0].kind).toBe(NotebookCellKind.Code);
        expect(cells[0].languageId).toBe(languageId);
        expect(cells[0].value).toBe('BODY');
    });

    it('is case-insensitive', () => {
        expect(read('```SQL\nselect 1\n```').cells[0].languageId).toBe('sql');
    });

    it('uses csharp-script, not csharp, so other C# tooling stays off notebook cells', () => {
        // Deliberate: C# Dev Kit / the Roslyn server attaching would double completions and
        // flag valid script-mode trailing expressions as errors.
        expect(read('```csharp\nvar x = 1;\n```').cells[0].languageId).toBe('csharp-script');
    });

    it('leaves an unknown fence as prose rather than guessing a language', () => {
        const cells = read('```python\nprint(1)\n```').cells;
        expect(cells.every((c) => c.kind === NotebookCellKind.Markup)).toBe(true);
    });
});

describe('cell language -> fence tag', () => {
    it.each([
        ['csharp-script', 'csharp'],
        ['http', 'http'],
        ['mermaid', 'mermaid'],
        ['powershell', 'powershell'],
        ['sql', 'sql'],
        ['dax', 'dax'],
    ])('a %s cell writes ```%s', (languageId, tag) => {
        expect(write([code('BODY', languageId)])).toContain('```' + tag + '\n');
    });
});

describe('round trip', () => {
    it('preserves markup, code and their order', () => {
        const md = ['# Title', '', '```sql', 'select 1', '```', '', 'Some prose.', '', '```dax', 'EVALUATE {1}', '```', ''].join('\n');
        const cells = read(md).cells;
        expect(cells.map((c) => c.languageId)).toEqual(['markdown', 'sql', 'markdown', 'dax']);
        expect(new TextDecoder().decode(serializer.serializeNotebook({ cells } as never))).toBe(md);
    });

    it('survives a second trip unchanged', () => {
        const md = '```powershell\nGet-Location\n```\n';
        const once = write(read(md).cells);
        expect(write(read(once).cells)).toBe(once);
    });

    it('handles CRLF input without leaving carriage returns in cells', () => {
        const cells = read('# T\r\n\r\n```sql\r\nselect 1\r\n```\r\n').cells;
        expect(cells.find((c) => c.languageId === 'sql')?.value).toBe('select 1');
    });

    it('keeps the content of an unterminated fence instead of dropping it', () => {
        const cells = read('```sql\nselect 1').cells;
        expect(cells[0].languageId).toBe('sql');
        expect(cells[0].value).toBe('select 1');
    });

    it('supports longer and tilde fences', () => {
        expect(read('````sql\nselect 1\n````').cells[0].languageId).toBe('sql');
        expect(read('~~~sql\nselect 1\n~~~').cells[0].languageId).toBe('sql');
    });
});
