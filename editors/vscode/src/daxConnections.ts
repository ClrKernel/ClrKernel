import * as vscode from 'vscode';
import { ClrKernelController } from './controller';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

// A leading "-- connections name" selector comment (valid DAX).
const CONNECTION_RE = /^\s*(?:--|\/\/)\s*(?:connections?|cube)\s*[:=]?\s*(\S+)/i;

interface CubeInfo {
    name: string;
    describe: string;
    isDefault: boolean;
}

interface ListResult {
    defaultName?: string;
    connections: CubeInfo[];
}

interface AddResult {
    ok: boolean;
    name?: string;
    error?: string;
}

/**
 * The cube (Analysis Services / Fabric) connection experience for #!dax cells: a
 * status-bar button showing the cube a cell runs against, and a guided QuickPick
 * to pick or add cubes — so users don't have to memorize the #!dax-connect syntax.
 * Cube connections are passwordless (Windows Integrated for on-prem SSAS, Microsoft
 * Entra for Azure AS / Fabric), so no secrets are collected here; for a SQL-login
 * cube use `#!dax-connect --user … --secret <env-var>`.
 */
export class DaxConnectionUi {
    private readonly changeEmitter = new vscode.EventEmitter<void>();

    constructor(private readonly controller: ClrKernelController) { }

    register(context: vscode.ExtensionContext): void {
        context.subscriptions.push(
            vscode.notebooks.registerNotebookCellStatusBarItemProvider(NOTEBOOK_TYPE, this.statusBarProvider()),
            vscode.commands.registerCommand('clrkernel.dax.selectConnection', (cell?: vscode.NotebookCell) => this.selectConnection(cell)),
            vscode.commands.registerCommand('clrkernel.dax.addConnection', (cell?: vscode.NotebookCell) => this.addConnection(cell)),
            this.changeEmitter,
        );
    }

    private statusBarProvider(): vscode.NotebookCellStatusBarItemProvider {
        return {
            onDidChangeCellStatusBarItems: this.changeEmitter.event,
            provideCellStatusBarItems: (cell) => {
                if (cell.document.languageId !== 'dax') {
                    return [];
                }
                const name = currentCube(cell.document.getText());
                const item = new vscode.NotebookCellStatusBarItem(
                    '$(server-environment) ' + (name ?? 'Select cube'),
                    vscode.NotebookCellStatusBarAlignment.Right,
                );
                item.command = {
                    title: 'Select cube',
                    command: 'clrkernel.dax.selectConnection',
                    arguments: [cell],
                };
                item.tooltip = 'Choose which cube this DAX cell runs against';
                return [item];
            },
        };
    }

    private resolveCell(cell?: vscode.NotebookCell): vscode.NotebookCell | undefined {
        if (cell) {
            return cell;
        }
        const editor = vscode.window.activeNotebookEditor;
        if (!editor) {
            return undefined;
        }
        return editor.notebook.cellAt(editor.selection.start);
    }

    private async selectConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }

        let list: ListResult;
        try {
            const client = await this.controller.getClient(cell.notebook);
            list = await client.request<ListResult>('clrkernel/dax/listConnections', {});
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }

        type Pick = vscode.QuickPickItem & { conn?: string; action?: 'add' };
        const picks: Pick[] = list.connections.map((c) => ({
            label: '$(server-environment) ' + c.name,
            description: c.describe + (c.isDefault ? '  •  default' : ''),
            conn: c.name,
        }));
        if (picks.length > 0) {
            picks.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
        }
        picks.push({ label: '$(add) Add cube…', action: 'add' });

        const pick = await vscode.window.showQuickPick(picks, {
            placeHolder: list.connections.length > 0 ? 'Run this DAX cell against…' : 'No cubes yet — add one',
        });
        if (!pick) {
            return;
        }
        if (pick.action === 'add') {
            await this.addConnection(cell);
            return;
        }
        if (pick.conn) {
            await applyCube(cell, pick.conn);
            this.changeEmitter.fire();
        }
    }

    private async addConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }

        const name = await vscode.window.showInputBox({
            title: 'New cube (1/3)',
            prompt: 'Cube name (used in cells, e.g. analytics)',
            validateInput: (v) => (/^\w[\w-]*$/.test(v) ? undefined : 'Use letters, digits, _ or -'),
        });
        if (!name) {
            return;
        }

        const typePick = await vscode.window.showQuickPick(
            [
                { label: 'On-prem SQL Server Analysis Services (Windows Integrated)', cubeKind: 'ssas' },
                { label: 'Microsoft Fabric / Power BI semantic model (Entra)', cubeKind: 'fabric' },
                { label: 'Azure Analysis Services (Entra)', cubeKind: 'azure-as' },
            ],
            { title: 'New cube (2/3)', placeHolder: 'Cube type' },
        );
        if (!typePick) {
            return;
        }

        let directive: string;
        if (typePick.cubeKind === 'fabric') {
            const workspace = await vscode.window.showInputBox({ title: 'New cube (3/3)', prompt: 'Fabric / Power BI workspace name', ignoreFocusOut: true });
            if (!workspace) {
                return;
            }
            const model = await vscode.window.showInputBox({ title: 'New cube (3/3)', prompt: 'Semantic model (dataset) name', ignoreFocusOut: true });
            if (!model) {
                return;
            }
            directive = `#!dax-connect --name ${quote(name)} --fabric --workspace ${quote(workspace)} --model ${quote(model)}`;
        } else {
            const serverPrompt = typePick.cubeKind === 'azure-as'
                ? 'Server (e.g. asazure://westus.asazure.windows.net/myserver)'
                : 'Server / host (e.g. ssas.db.local)';
            const server = await vscode.window.showInputBox({ title: 'New cube (3/3)', prompt: serverPrompt, ignoreFocusOut: true });
            if (!server) {
                return;
            }
            const database = await vscode.window.showInputBox({ title: 'New cube (3/3)', prompt: 'Model / database name', ignoreFocusOut: true });
            if (!database) {
                return;
            }
            const authFlag = typePick.cubeKind === 'azure-as' ? ' --azure-as' : '';
            directive = `#!dax-connect --name ${quote(name)} --server ${quote(server)} --database ${quote(database)}${authFlag}`;
        }

        try {
            const client = await this.controller.getClient(cell.notebook);
            const result = await client.request<AddResult>('clrkernel/dax/addConnection', { directive });
            if (!result.ok) {
                void vscode.window.showErrorMessage('Could not add cube: ' + (result.error ?? 'unknown error'));
                return;
            }
        } catch (e) {
            void vscode.window.showErrorMessage('Could not add cube: ' + errorText(e));
            return;
        }

        await applyCube(cell, name);
        this.changeEmitter.fire();
        void vscode.window.showInformationMessage(`Cube '${name}' is ready.`);
    }
}

function currentCube(text: string): string | undefined {
    for (const raw of text.replace(/\r\n/g, '\n').split('\n')) {
        const line = raw.trim();
        if (line.length === 0) {
            continue;
        }
        const m = CONNECTION_RE.exec(line);
        return m ? m[1] : undefined; // first non-blank line decides
    }
    return undefined;
}

async function applyCube(cell: vscode.NotebookCell, name: string): Promise<void> {
    const doc = cell.document;
    const edit = new vscode.WorkspaceEdit();
    const selectorLine = `-- connections ${name}`;
    const firstLine = doc.lineCount > 0 ? doc.lineAt(0) : undefined;
    if (firstLine && CONNECTION_RE.test(firstLine.text)) {
        edit.replace(doc.uri, firstLine.range, selectorLine);
    } else {
        edit.insert(doc.uri, new vscode.Position(0, 0), selectorLine + '\n');
    }
    await vscode.workspace.applyEdit(edit);
}

function quote(value: string): string {
    return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

function errorText(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
}
