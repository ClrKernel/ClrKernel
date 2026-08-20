import * as vscode from 'vscode';
import { ConnectionUi } from './connections';
import { ClrKernelController } from './controller';
import { nextUntitledNotebookName } from './directives';
import { MarkdownNotebookSerializer } from './markdownSerializer';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

export function activate(context: vscode.ExtensionContext): void {
    const controller = new ClrKernelController(NOTEBOOK_TYPE);
    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer(NOTEBOOK_TYPE, new MarkdownNotebookSerializer()),
        // Read-only virtual documents for Go to Definition on metadata symbols:
        // the server decompiles the type; the .cs path gives C# highlighting.
        vscode.workspace.registerTextDocumentContentProvider('clrkernel-metadata', {
            provideTextDocumentContent: (uri) => controller.metadataSource(uri.path.replace(/^\//, '')),
        }),
        controller,
        vscode.commands.registerCommand('clrkernel.newNotebook', createNewNotebook),
        vscode.commands.registerCommand('clrkernel.restartKernel', async () => {
            const notebook = vscode.window.activeNotebookEditor?.notebook;
            await controller.restart(notebook);
            void vscode.window.showInformationMessage(notebook
                ? `ClrKernel restarted for ${notebook.uri.path.split('/').pop()}. Its variables, connections and PowerShell state are cleared; other notebooks are untouched.`
                : 'ClrKernel restarted. Variables, connections and PowerShell state are cleared; the next cell run starts a fresh kernel.');
        }),
    );

    // One connection button + schema-driven wizard for every language whose
    // descriptor declares connections — SQL, DAX, and any provider plugged in
    // later, with no per-language UI code.
    new ConnectionUi(controller).register(context);
}

/**
 * Opens a fresh untitled ClrKernel markdown notebook with one empty C# cell.
 *
 * Named `Untitled-N.nb.md` rather than letting the editor choose. Passing the notebook *type* gets
 * a document the editor names from the type's selector, which keeps only the last extension — so
 * it offers `Untitled.md`, which is not a name this notebook type claims, and saving it needs the
 * `.nb.md` typed in by hand. Opening an `untitled:` URI instead fixes the name up front.
 *
 * That overload takes no initial content, so the first cell is inserted afterwards. If it fails for
 * any reason the original call is used, which is worse only in the name it suggests.
 */
async function createNewNotebook(): Promise<void> {
    // Something runnable rather than an empty cell. The language server starts on the first
    // execution, so completions and hover are dead until then — a blank cell invites typing into
    // a notebook that cannot help yet, while this one is a keystroke from waking it up.
    const cell = new vscode.NotebookCellData(
        vscode.NotebookCellKind.Code, 'Console.WriteLine("Hello World!");', 'csharp-script');
    const name = nextUntitledNotebookName(vscode.workspace.notebookDocuments.map((d) => d.uri.path));

    try {
        const notebook = await vscode.workspace.openNotebookDocument(vscode.Uri.parse(`untitled:${name}`));
        if (notebook.cellCount === 0) {
            const edit = new vscode.WorkspaceEdit();
            edit.set(notebook.uri, [vscode.NotebookEdit.insertCells(0, [cell])]);
            await vscode.workspace.applyEdit(edit);
        }
        await vscode.window.showNotebookDocument(notebook);
        return;
    } catch {
        // Fall through: an untitled notebook named by the editor still works.
    }

    const notebook = await vscode.workspace.openNotebookDocument(NOTEBOOK_TYPE, new vscode.NotebookData([cell]));
    await vscode.window.showNotebookDocument(notebook);
}

export function deactivate(): void {
    // controller disposal (registered as a subscription) shuts the server down
}
