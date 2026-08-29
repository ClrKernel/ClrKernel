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

    deserializeNotebook(content: Uint8Array): vscode.NotebookData {
        const text = new TextDecoder().decode(content);
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
        const parts: string[] = [];
        for (const cell of data.cells) {
            if (cell.kind === vscode.NotebookCellKind.Code) {
                const descriptor = languageForEditorLanguage(cell.languageId);
                const tag = descriptor ? tagForCell(descriptor, cell.value) : 'csharp';
                parts.push('```' + tag + '\n' + cell.value.replace(/\s+$/, '') + '\n```');
            } else {
                parts.push(cell.value.replace(/\s+$/, ''));
            }
        }
        return new TextEncoder().encode(parts.join('\n\n') + '\n');
    }
}
