import * as vscode from 'vscode';

/**
 * Executable markdown <-> notebook. Fenced code blocks tagged csharp/c#/cs
 * become C# code cells, http fences HTTP (.http) cells, mermaid fences Mermaid
 * cells, and powershell/pwsh/ps1 fences PowerShell cells;
 * everything between them becomes markup cells. The same files run headlessly
 * via ClrKernel's `#!import` and in CI, so serialization round-trips cleanly.
 */
export class MarkdownNotebookSerializer implements vscode.NotebookSerializer {
    private static readonly fenceOpen = /^(`{3,}|~{3,})\s*(csharp|c#|cs|http|mermaid|powershell|pwsh|ps1|sql|tsql|dax)\s*$/i;

    // Cell languageId for a fence tag; also the tag emitted when serializing.
    private static languageForTag(tag: string): string {
        const t = tag.toLowerCase();
        if (t === 'http') { return 'http'; }
        if (t === 'mermaid') { return 'mermaid'; }
        if (t === 'powershell' || t === 'pwsh' || t === 'ps1') { return 'powershell'; }
        if (t === 'sql' || t === 'tsql') { return 'sql'; }
        return 'csharp';
    }

    deserializeNotebook(content: Uint8Array): vscode.NotebookData {
        const text = new TextDecoder().decode(content);
        const cells: vscode.NotebookCellData[] = [];

        let markup: string[] = [];
        let code: string[] | null = null;
        let closingFence = '';
        let language = 'csharp';

        const flushMarkup = () => {
            const value = markup.join('\n').trim();
            if (value.length > 0) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Markup, value, 'markdown'));
            }
            markup = [];
        };

        for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
            if (code === null) {
                const match = MarkdownNotebookSerializer.fenceOpen.exec(line);
                if (match) {
                    flushMarkup();
                    code = [];
                    closingFence = match[1];
                    language = MarkdownNotebookSerializer.languageForTag(match[2]);
                } else {
                    markup.push(line);
                }
            } else if (line.trim() === closingFence) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, code.join('\n'), language));
                code = null;
            } else {
                code.push(line);
            }
        }
        if (code !== null) {
            // unterminated fence: keep the content as a code cell rather than losing it
            cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, code.join('\n'), language));
        }
        flushMarkup();

        return new vscode.NotebookData(cells);
    }

    serializeNotebook(data: vscode.NotebookData): Uint8Array {
        const parts: string[] = [];
        for (const cell of data.cells) {
            if (cell.kind === vscode.NotebookCellKind.Code) {
                const tag = cell.languageId === 'http' ? 'http'
                    : cell.languageId === 'mermaid' ? 'mermaid'
                    : cell.languageId === 'sql' ? 'sql'
                parts.push('```' + tag + '\n' + cell.value.replace(/\s+$/, '') + '\n```');
            } else {
                parts.push(cell.value.replace(/\s+$/, ''));
            }
        }
        return new TextEncoder().encode(parts.join('\n\n') + '\n');
    }
}
