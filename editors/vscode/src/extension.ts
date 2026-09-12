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
        // A .dib opens as this notebook type and runs as it is; the offer to
        // convert is made once per open, and "keep" is remembered for the session.
        vscode.workspace.onDidOpenNotebookDocument((notebook) => void offerConversion(notebook)),
        vscode.commands.registerCommand('clrkernel.convertToMarkdown', async () => {
            const notebook = vscode.window.activeNotebookEditor?.notebook;
            if (!notebook) {
                void vscode.window.showInformationMessage('Open a notebook first — this writes its .nb.md beside it.');
                return;
            }
            await convertToMarkdown(notebook);
        }),
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

const keptAsIs = new Set<string>();

/**
 * Which notebooks the offer is made for: a .dib opened as this type, and an
 * .ipynb whose kernelspec is ClrKernel's — the Jupyter extension owns .ipynb, so
 * that one runs there already, and a notebook written for some other kernel is
 * not asked about at all.
 */
function conversionOffer(notebook: vscode.NotebookDocument): { format: string; keep: string } | null {
    if (notebook.uri.scheme !== 'file') {
        return null;
    }
    if (notebook.notebookType === NOTEBOOK_TYPE && /\.dib$/i.test(notebook.uri.path)) {
        return { format: 'a Polyglot notebook', keep: 'Keep as .dib' };
    }
    if (/\.ipynb$/i.test(notebook.uri.path)) {
        const metadata = notebook.metadata as { metadata?: { kernelspec?: { name?: string } }; custom?: { metadata?: { kernelspec?: { name?: string } } } };
        const kernel = metadata?.metadata?.kernelspec?.name ?? metadata?.custom?.metadata?.kernelspec?.name ?? '';
        if (/clrkernel/i.test(kernel)) {
            return { format: 'a Jupyter notebook on ClrKernel', keep: 'Keep as .ipynb' };
        }
    }
    return null;
}

/**
 * The notebook as the .nb.md beside it — the same conversion as `clrkernel
 * convert`, offered once where the file is opened. Outputs are not carried over,
 * the original is left in place, and an existing target is never overwritten.
 */
async function offerConversion(notebook: vscode.NotebookDocument): Promise<void> {
    const offer = conversionOffer(notebook);
    if (!offer || keptAsIs.has(notebook.uri.toString())) {
        return;
    }
    const name = notebook.uri.path.split('/').pop() ?? notebook.uri.path;
    const targetName = markdownTarget(notebook).path.split('/').pop() ?? '';
    const choice = await vscode.window.showInformationMessage(
        `${name} is ${offer.format}. It runs here as it is; convert it to ${targetName}, which reviews like source?`,
        'Convert', offer.keep);
    if (choice !== 'Convert') {
        keptAsIs.add(notebook.uri.toString());
        return;
    }
    await convertToMarkdown(notebook);
}

function markdownTarget(notebook: vscode.NotebookDocument): vscode.Uri {
    return notebook.uri.with({ path: notebook.uri.path.replace(/\.[^./]+$/, '') + '.nb.md' });
}

/** Writes the open notebook's cells as the .nb.md beside it and opens that. */
async function convertToMarkdown(notebook: vscode.NotebookDocument): Promise<void> {
    if (notebook.uri.scheme !== 'file' || /\.nb\.md$/i.test(notebook.uri.path)) {
        void vscode.window.showInformationMessage('That is already executable markdown — there is nothing to convert.');
        return;
    }
    const name = notebook.uri.path.split('/').pop() ?? notebook.uri.path;
    const target = markdownTarget(notebook);
    const targetName = target.path.split('/').pop() ?? target.path;
    try {
        await vscode.workspace.fs.stat(target);
        void vscode.window.showWarningMessage(`${targetName} already exists; nothing was written.`);
        return;
    } catch {
        // Not there — which is the case this is for.
    }
    const data = new vscode.NotebookData(notebook.getCells().map((cell) => {
        const copy = new vscode.NotebookCellData(cell.kind, cell.document.getText(), cell.document.languageId);
        copy.metadata = cell.metadata; // carries a kernel-less section's own tag (#!kql)
        return copy;
    }));
    await vscode.workspace.fs.writeFile(target, new TextEncoder().encode(MarkdownNotebookSerializer.toMarkdown(data)));
    await vscode.window.showNotebookDocument(await vscode.workspace.openNotebookDocument(target));
    void vscode.window.showInformationMessage(`Wrote ${targetName}. ${name} is still there — delete it when you are done with it.`);
}

export function deactivate(): void {
    // controller disposal (registered as a subscription) shuts the server down
}
