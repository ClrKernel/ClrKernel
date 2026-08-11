import * as vscode from 'vscode';
import { ClrKernelController } from './controller';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

// A leading "-- connections name" selector comment (valid DAX).
const CONNECTION_RE = /^\s*(?:--|\/\/)\s*(?:connections?|cube)\s*[:=]?\s*(\S+)/i;

interface CubeInfo {
    name: string;
    describe: string;
    server?: string;
    database?: string;
    auth?: string;
    isDefault: boolean;
}

type CubeKind = 'ssas' | 'fabric' | 'azure-as';

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
            vscode.commands.registerCommand('clrkernel.dax.editConnection', (cell?: vscode.NotebookCell) => this.editConnection(cell)),
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
            list = await client.request<ListResult>('clrkernel/connections/list', { languageId: 'dax' });
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }

        type Pick = vscode.QuickPickItem & { conn?: string; action?: 'add' | 'edit' };
        const picks: Pick[] = list.connections.map((c) => ({
            label: '$(server-environment) ' + c.name,
            description: c.describe + (c.isDefault ? '  •  default' : ''),
            conn: c.name,
        }));
        if (picks.length > 0) {
            picks.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
        }
        picks.push({ label: '$(add) Add cube…', action: 'add' });
        if (list.connections.length > 0) {
            picks.push({ label: '$(edit) Edit cube…', action: 'edit' });
        }

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
        if (pick.action === 'edit') {
            await this.editConnection(cell);
            return;
        }
        if (pick.conn) {
            await applyCube(cell, pick.conn);
            this.changeEmitter.fire();
        }
    }

    /** Pick an existing cube and re-run the wizard pre-filled with its settings. */
    private async editConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }

        let list: ListResult;
        try {
            const client = await this.controller.getClient(cell.notebook);
            list = await client.request<ListResult>('clrkernel/connections/list', { languageId: 'dax' });
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }
        if (list.connections.length === 0) {
            void vscode.window.showInformationMessage('There are no cubes to edit yet.');
            return;
        }

        let target = list.connections[0];
        if (list.connections.length > 1) {
            const current = currentCube(cell.document.getText());
            const ordered = [...list.connections].sort((a, b) =>
                (a.name === current ? -1 : 0) - (b.name === current ? -1 : 0));
            const pick = await vscode.window.showQuickPick(
                ordered.map((c) => ({
                    label: '$(server-environment) ' + c.name,
                    description: c.describe + (c.name === current ? '  •  this cell' : ''),
                    conn: c,
                })),
                { title: 'Edit cube', placeHolder: 'Which cube?' },
            );
            if (!pick) {
                return;
            }
            target = pick.conn;
        }

        await this.runCubeWizard(cell, target);
    }

    private async addConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (cell) {
            await this.runCubeWizard(cell);
        }
    }

    // Add (existing == null) and Edit (existing set) share this wizard. When editing,
    // the name is fixed and the cube type / server / model are pre-filled; re-registering
    // overwrites the cube in place. Cube connections are passwordless, so no secret.
    private async runCubeWizard(cell: vscode.NotebookCell, existing?: CubeInfo): Promise<void> {
        const editing = existing !== undefined;
        const verb = editing ? 'Edit cube' : 'New cube';
        const prefill = existing ? describeExisting(existing) : undefined;

        let name = existing?.name ?? '';
        if (!editing) {
            const entered = await vscode.window.showInputBox({
                title: `${verb} (1/3)`,
                prompt: 'Cube name (used in cells, e.g. analytics)',
                validateInput: (v) => (/^\w[\w-]*$/.test(v) ? undefined : 'Use letters, digits, _ or -'),
            });
            if (!entered) {
                return;
            }
            name = entered;
        }

        const typeOptions: { label: string; cubeKind: CubeKind }[] = [
            { label: 'On-prem SQL Server Analysis Services (Windows Integrated)', cubeKind: 'ssas' },
            { label: 'Microsoft Fabric / Power BI semantic model (Entra)', cubeKind: 'fabric' },
            { label: 'Azure Analysis Services (Entra)', cubeKind: 'azure-as' },
        ];
        const orderedTypes = prefill
            ? [...typeOptions].sort((a, b) => (a.cubeKind === prefill.kind ? -1 : 0) - (b.cubeKind === prefill.kind ? -1 : 0))
                .map((o) => (o.cubeKind === prefill.kind ? { ...o, description: 'current' } : o))
            : typeOptions;
        const typePick = await vscode.window.showQuickPick(orderedTypes, {
            title: `${verb} (2/3)`,
            placeHolder: 'Cube type',
        });
        if (!typePick) {
            return;
        }

        // Only reuse the pre-filled field values when the type is unchanged.
        const same = prefill?.kind === typePick.cubeKind;

        let directive: string;
        if (typePick.cubeKind === 'fabric') {
            const workspace = await vscode.window.showInputBox({
                title: `${verb} (3/3)`, prompt: 'Fabric / Power BI workspace name',
                value: same ? prefill?.workspace : undefined, ignoreFocusOut: true,
            });
            if (!workspace) {
                return;
            }
            const model = await vscode.window.showInputBox({
                title: `${verb} (3/3)`, prompt: 'Semantic model (dataset) name',
                value: same ? prefill?.model : undefined, ignoreFocusOut: true,
            });
            if (!model) {
                return;
            }
            directive = `#!dax-connect --name ${quote(name)} --fabric --workspace ${quote(workspace)} --model ${quote(model)}`;
        } else {
            const serverPrompt = typePick.cubeKind === 'azure-as'
                ? 'Server (e.g. asazure://westus.asazure.windows.net/myserver)'
                : 'Server / host (e.g. DataWarehouseServer01.yourdomain.local)';
            const server = await vscode.window.showInputBox({
                title: `${verb} (3/3)`, prompt: serverPrompt,
                value: same ? prefill?.server : undefined, ignoreFocusOut: true,
            });
            if (!server) {
                return;
            }
            const database = await vscode.window.showInputBox({
                title: `${verb} (3/3)`, prompt: 'Model / database name',
                value: same ? prefill?.model : undefined, ignoreFocusOut: true,
            });
            if (!database) {
                return;
            }
            const authFlag = typePick.cubeKind === 'azure-as' ? ' --azure-as' : '';
            directive = `#!dax-connect --name ${quote(name)} --server ${quote(server)} --database ${quote(database)}${authFlag}`;
        }

        try {
            const client = await this.controller.getClient(cell.notebook);
            const result = await client.request<AddResult>('clrkernel/connections/add', { languageId: 'dax', directive });
            if (!result.ok) {
                void vscode.window.showErrorMessage(`Could not ${editing ? 'update' : 'add'} cube: ` + (result.error ?? 'unknown error'));
                return;
            }
        } catch (e) {
            void vscode.window.showErrorMessage(`Could not ${editing ? 'update' : 'add'} cube: ` + errorText(e));
            return;
        }

        if (!editing) {
            await applyCube(cell, name);
        }
        this.changeEmitter.fire();
        void vscode.window.showInformationMessage(editing ? `Cube '${name}' updated.` : `Cube '${name}' is ready.`);
    }
}

/** Recovers the wizard's cube type + fields from a stored cube's server/database. */
function describeExisting(c: CubeInfo): { kind: CubeKind; server?: string; workspace?: string; model?: string } {
    const server = c.server ?? '';
    const lower = server.toLowerCase();
    if (lower.startsWith('powerbi://')) {
        const marker = '/myorg/';
        const idx = lower.indexOf(marker);
        const workspace = idx >= 0 ? server.substring(idx + marker.length) : '';
        return { kind: 'fabric', workspace, model: c.database };
    }
    if (lower.startsWith('asazure://')) {
        return { kind: 'azure-as', server, model: c.database };
    }
    return { kind: 'ssas', server, model: c.database };
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
