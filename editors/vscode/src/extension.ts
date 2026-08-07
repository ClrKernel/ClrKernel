import * as vscode from 'vscode';
import { ClrKernelController } from './controller';
import { MarkdownNotebookSerializer } from './markdownSerializer';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

export function activate(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer(NOTEBOOK_TYPE, new MarkdownNotebookSerializer()),
        new ClrKernelController(NOTEBOOK_TYPE),
    );
}

export function deactivate(): void {
    // controller disposal (registered as a subscription) shuts the server down
}
