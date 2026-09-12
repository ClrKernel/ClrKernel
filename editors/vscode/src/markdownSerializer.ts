import * as vscode from 'vscode';
import { editorLanguageFor, isCSharpTag, languageForEditorLanguage, languageForTag, selectorForTag, tagForCell } from './languages';

/**
 * Executable markdown <-> notebook, driven by the kernel's language descriptors:
 * a tagged block whose tag a registered language claims becomes a code cell in
 * that language; csharp/c#/cs blocks become C# cells; everything else becomes
 * markup. The same files run headlessly via ClrKernel's `#!import` and the Jobs
 * runner — all three parse with the same descriptor model, so serialization
 * round-trips cleanly.
 *
 * A serializer runs at file open, possibly before any server has started: until
 * the handshake delivers the live list, the bundled defaults (all shipped
 * languages) apply, so only blocks of runtime-plugged languages deserialize as
 * markup until the kernel is up and the file reopened.
 */
export class MarkdownNotebookSerializer implements vscode.NotebookSerializer {
    private static readonly blockOpen = /^(`{3,}|~{3,})\s*([^\s`~]+)\s*$/;
    /** A bare `#!name` line: a .dib section marker. One with arguments is a directive inside its section. */
    private static readonly dibSection = /^#!([^\s]+)\s*$/;
    private static readonly csharpSections = new Set(['csharp', 'c#', 'cs']);
    private static readonly proseSections = new Set(['markdown', 'md']);
    private static readonly dibMeta =
        '#!meta\n\n{"kernelInfo":{"defaultKernelName":"csharp","items":[{"aliases":[],"name":"csharp"}]}}\n\n';

    /**
     * A .dib and a .nb.md are both this notebook type — a serializer never sees the
     * file name, so the content says which it is: a Polyglot file opens with a
     * section marker (`#!meta`, `#!csharp`), and no markdown a person wrote does.
     */
    static isDib(text: string): boolean {
        const first = text.replace(/\r\n/g, '\n').split('\n').find((l) => l.trim().length > 0) ?? '';
        return MarkdownNotebookSerializer.dibSection.test(first.trim());
    }

    deserializeNotebook(content: Uint8Array): vscode.NotebookData {
        const text = new TextDecoder().decode(content);
        return MarkdownNotebookSerializer.isDib(text)
            ? MarkdownNotebookSerializer.readDib(text)
            : MarkdownNotebookSerializer.readMarkdown(text);
    }

    /**
     * Sections at their `#!` markers. `#!meta` is Polyglot's kernelInfo and is dropped;
     * `#!markdown` is prose; C# and every language the kernel claims are code cells;
     * a section in a language it does not (`#!fsharp`, `#!kql`, `#!value`) stays a
     * code cell under its own tag, so it saves back as it was rather than vanishing.
     * The data is marked `format: 'dib'` so it serializes back as one.
     */
    private static readDib(text: string): vscode.NotebookData {
        const cells: vscode.NotebookCellData[] = [];
        let tag: string | null = 'csharp'; // content before the first marker is C#
        let lines: string[] = [];

        const flush = () => {
            const value = lines.join('\n').trim();
            lines = [];
            if (value.length === 0 || tag === null || tag === 'meta') {
                return;
            }
            if (MarkdownNotebookSerializer.proseSections.has(tag)) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Markup, value, 'markdown'));
                return;
            }
            const descriptor = isCSharpTag(tag) ? undefined : languageForTag(tag);
            const language = isCSharpTag(tag) ? 'csharp-script' : descriptor ? editorLanguageFor(descriptor) : tag;
            const selector = descriptor ? selectorForTag(descriptor, tag) : null;
            const needsSelector = descriptor && selector !== descriptor.defaultSelector
                && tag.toLowerCase() !== descriptor.id.toLowerCase() && !/^\s*#!/.test(value);
            const cell = new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code, needsSelector ? selector + '\n' + value : value, language);
            if (!descriptor && !isCSharpTag(tag)) {
                cell.metadata = { dibTag: tag };
            }
            cells.push(cell);
        };

        for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
            const marker = MarkdownNotebookSerializer.dibSection.exec(line.trim());
            if (marker) {
                flush();
                tag = marker[1].toLowerCase();
            } else {
                lines.push(line);
            }
        }
        flush();

        const data = new vscode.NotebookData(cells);
        data.metadata = { format: 'dib' };
        return data;
    }

    private static readMarkdown(text: string): vscode.NotebookData {
        const cells: vscode.NotebookCellData[] = [];

        let markup: string[] = [];
        let code: string[] | null = null;
        let closingDelimiter = '';
        let language = 'csharp-script';
        let pendingSelector: string | null = null;

        const flushMarkup = () => {
            const value = markup.join('\n').trim();
            if (value.length > 0) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Markup, value, 'markdown'));
            }
            markup = [];
        };

        const withSelector = (value: string): string =>
            pendingSelector && !/^\s*#!/.test(value) ? pendingSelector + '\n' + value : value;

        for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
            if (code === null) {
                const match = MarkdownNotebookSerializer.blockOpen.exec(line);
                const descriptor = match && !isCSharpTag(match[2]) ? languageForTag(match[2]) : undefined;
                if (match && (descriptor || isCSharpTag(match[2]))) {
                    flushMarkup();
                    code = [];
                    closingDelimiter = match[1];
                    // The editor's id for it, not the kernel's: the two differ for a
                    // language that took one of its own, and this is the id the cell
                    // will carry for as long as it is open.
                    language = descriptor ? editorLanguageFor(descriptor) : 'csharp-script';
                    // A tag with its own selector keeps it explicit in the cell (#!zsh)
                    // so its meaning survives execution under the language default; a
                    // tag matching the default — or spelling the language's own id, its
                    // canonical alias (```powershell) — needs no line.
                    const selector = descriptor ? selectorForTag(descriptor, match[2]) : null;
                    pendingSelector = descriptor && selector !== descriptor.defaultSelector &&
                        match[2].toLowerCase() !== descriptor.id.toLowerCase() ? selector : null;
                } else {
                    // Unknown-language blocks (```python) stay markup, delimiters included.
                    markup.push(line);
                }
            } else if (line.trim() === closingDelimiter) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, withSelector(code.join('\n')), language));
                code = null;
                pendingSelector = null;
            } else {
                code.push(line);
            }
        }
        if (code !== null) {
            // unterminated block: keep the content as a code cell rather than losing it
            cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, withSelector(code.join('\n')), language));
        }
        flushMarkup();

        return new vscode.NotebookData(cells);
    }

    serializeNotebook(data: vscode.NotebookData): Uint8Array {
        return new TextEncoder().encode(data.metadata?.format === 'dib'
            ? MarkdownNotebookSerializer.writeDib(data)
            : MarkdownNotebookSerializer.writeMarkdown(data));
    }

    /** The cells as a .dib again: the header, then one `#!tag` section per cell. */
    private static writeDib(data: vscode.NotebookData): string {
        const parts: string[] = [];
        for (const cell of data.cells) {
            const tag = cell.kind !== vscode.NotebookCellKind.Code ? 'markdown' : MarkdownNotebookSerializer.tagOf(cell);
            // The section marker already says #!zsh; a selector line repeating it
            // would read as an empty section on the way back in.
            const value = cell.value.replace(/\s+$/, '').replace(new RegExp('^\\s*#!' + tag + '[ \\t]*\\n?', 'i'), '');
            parts.push('#!' + tag + '\n\n' + value);
        }
        return MarkdownNotebookSerializer.dibMeta + parts.join('\n\n') + '\n';
    }

    /** What `.nb.md` conversion writes for an open notebook — the markdown form, whatever the source was. */
    static toMarkdown(data: vscode.NotebookData): string {
        return MarkdownNotebookSerializer.writeMarkdown(data);
    }

    /** The tag to write: a .dib section's own for a kernel this one does not have (`kql`), else the language's. */
    private static tagOf(cell: vscode.NotebookCellData): string {
        if (typeof cell.metadata?.dibTag === 'string') {
            return cell.metadata.dibTag;
        }
        const descriptor = languageForEditorLanguage(cell.languageId);
        return descriptor ? tagForCell(descriptor, cell.value) : 'csharp';
    }

    private static writeMarkdown(data: vscode.NotebookData): string {
        const parts: string[] = [];
        for (const cell of data.cells) {
            if (cell.kind === vscode.NotebookCellKind.Code) {
                parts.push('```' + MarkdownNotebookSerializer.tagOf(cell) + '\n' + cell.value.replace(/\s+$/, '') + '\n```');
            } else {
                parts.push(cell.value.replace(/\s+$/, ''));
            }
        }
        return parts.join('\n\n') + '\n';
    }
}
