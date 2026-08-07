import * as vscode from 'vscode';

/**
 * Executable markdown <-> notebook. Fenced code blocks tagged csharp/c#/cs
 * become code cells; everything between them becomes markup cells. The same
 * files run headlessly via ClrKernel's `#!import` and in CI, so serialization
 * is careful to round-trip cleanly.
 */
export class MarkdownNotebookSerializer implements vscode.NotebookSerializer {
    private static readonly fenceOpen = /^(`{3,}|~{3,})\s*(csharp|c#|cs)\s*$/i;

    deserializeNotebook(content: Uint8Array): vscode.NotebookData {
        const text = new TextDecoder().decode(content);
        const cells: vscode.NotebookCellData[] = [];

        let markup: string[] = [];
        let code: string[] | null = null;
        let closingFence = '';

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
                } else {
                    markup.push(line);
                }
            } else if (line.trim() === closingFence) {
                cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, code.join('\n'), 'csharp'));
                code = null;
            } else {
                code.push(line);
            }
        }
        if (code !== null) {
            // unterminated fence: keep the content as a code cell rather than losing it
            cells.push(new vscode.NotebookCellData(vscode.NotebookCellKind.Code, code.join('\n'), 'csharp'));
        }
        flushMarkup();

        return new vscode.NotebookData(cells);
    }

    serializeNotebook(data: vscode.NotebookData): Uint8Array {
        const parts: string[] = [];
        for (const cell of data.cells) {
            if (cell.kind === vscode.NotebookCellKind.Code) {
                parts.push('```csharp\n' + cell.value.replace(/\s+$/, '') + '\n```');
            } else {
                parts.push(cell.value.replace(/\s+$/, ''));
            }
        }
        return new TextEncoder().encode(parts.join('\n\n') + '\n');
    }
}
