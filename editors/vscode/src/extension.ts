import * as vscode from 'vscode';
import { ClrKernelController } from './controller';
import { MarkdownNotebookSerializer } from './markdownSerializer';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

export function activate(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer(NOTEBOOK_TYPE, new MarkdownNotebookSerializer()),
        new ClrKernelController(NOTEBOOK_TYPE),
        vscode.commands.registerCommand('clrkernel.newNotebook', createNewNotebook),
    );
}

/** Opens a fresh untitled ClrKernel markdown notebook with one empty C# cell. */
async function createNewNotebook(): Promise<void> {
    const cell = new vscode.NotebookCellData(vscode.NotebookCellKind.Code, '', 'csharp');
    const data = new vscode.NotebookData([cell]);
    const notebook = await vscode.workspace.openNotebookDocument(NOTEBOOK_TYPE, data);
    await vscode.window.showNotebookDocument(notebook);
}

export function deactivate(): void {
    // controller disposal (registered as a subscription) shuts the server down
}
