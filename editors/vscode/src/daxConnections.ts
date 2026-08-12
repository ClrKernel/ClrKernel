import * as path from 'path';
import * as vscode from 'vscode';
import { ClrKernelController } from './controller';
import { buildDaxConnectDirective } from './directives';

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

interface ConfigStatusResult {
    ok: boolean;
    found: boolean;
    path?: string;
    names: string[];
    error?: string;
}

interface SaveConfigResult {
    ok: boolean;
    path?: string;
    error?: string;
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

        // Bring in any saved connections.json cubes so they appear in the list, the same way the
        // SQL picker does — otherwise a saved cube is invisible until a cell runs.
        await this.controller.ensureConnectionsConfigLoaded(cell.notebook);

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
    /**
     * Which credential to use for an Entra-backed cube.
     *
     * Both work, and which one a tenant accepts is the tenant's decision. A token fetched by the
     * kernel comes from a generic developer application, and a tenant under conditional access may
     * refuse it however correct its scope is. The Windows identity sidesteps that — the endpoint
     * negotiates with the signed-in account, so no sign-in prompt and no app registration are
     * involved — but SSPI is Windows-only, so it is offered first there and not at all elsewhere.
     *
     * Returns the flag to append, which is legitimately an EMPTY STRING for the Entra token (it
     * is the default, so it needs no flag), or undefined if the user cancelled. Callers must test
     * `=== undefined`: `if (!flag)` treats the empty string as a cancellation and silently drops
     * the connection, which is exactly what happened when this was first written.
     */
    private async pickEntraAuth(what: string): Promise<string | undefined> {
        if (process.platform !== 'win32') {
            return ''; // no SSPI off Windows; the Entra token is the only option, and needs no flag
        }
        type AuthPick = vscode.QuickPickItem & { flag: string };
        const pick = await vscode.window.showQuickPick<AuthPick>(
            [
                {
                    label: '$(shield) Windows identity',
                    description: 'Integrated Security=SSPI',
                    detail: 'Uses your signed-in Windows account. Best on a domain- or Entra-joined machine; no sign-in prompt.',
                    flag: ' --integrated',
                },
                {
                    label: '$(key) Microsoft Entra sign-in',
                    description: 'access token',
                    detail: 'Signs in with your Azure identity (az login, or a browser prompt). Needed when the machine is not Entra-joined.',
                    flag: '',
                },
            ],
            { title: `${what} — how should ClrKernel authenticate?`, ignoreFocusOut: true },
        );
        return pick?.flag;
    }

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
            const auth = await this.pickEntraAuth('Fabric / Power BI');
            if (auth === undefined) {
                return; // cancelled. An empty string is a valid answer: the Entra token needs no flag.
            }
            directive = buildDaxConnectDirective({
                name, kind: 'fabric', workspace, model, integrated: auth === ' --integrated',
            });
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
            let integrated = false;
            if (typePick.cubeKind === 'azure-as') {
                const auth = await this.pickEntraAuth('Azure Analysis Services');
                if (auth === undefined) {
                    return; // cancelled; '' means the Entra token, which needs no flag
                }
                integrated = auth === ' --integrated';
            }
            directive = buildDaxConnectDirective({
                name, kind: typePick.cubeKind === 'azure-as' ? 'azure-as' : 'on-prem',
                server, database, integrated,
            });
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

        await this.promptSaveToConfig(cell, name);
    }

    /**
     * Offers to persist the cube to a connections.json so it reloads automatically next session.
     * The same file the SQL connections use — entries carry a $type, so one file holds both — and
     * as there, a password is written as a reference, never as itself.
     */
    private async promptSaveToConfig(cell: vscode.NotebookCell, name: string): Promise<void> {
        let client;
        try {
            client = await this.controller.getClient(cell.notebook);
        } catch {
            return; // server not reachable — nothing to offer
        }

        const directory = path.dirname(cell.notebook.uri.fsPath);
        let status: ConfigStatusResult;
        try {
            status = await client.request<ConfigStatusResult>(
                'clrkernel/connections/configStatus', { languageId: 'dax', directory });
        } catch {
            return; // couldn't check — don't nag
        }
        if (!status.ok) {
            return;
        }

        type SavePick = vscode.QuickPickItem & { action: 'existing' | 'choose' | 'skip' };
        const picks: SavePick[] = [];
        if (status.found && status.path) {
            picks.push({
                label: '$(save) Save to this file',
                description: status.path,
                detail: status.names.length ? `Existing connections: ${status.names.join(', ')}` : 'No connections yet',
                action: 'existing',
            });
        }
        picks.push({
            label: '$(new-file) Choose a file…',
            description: status.found ? 'a different connections.json' : 'no connections.json found nearby',
            action: 'choose',
        });
        picks.push({ label: "Don't save", action: 'skip' });

        const pick = await vscode.window.showQuickPick(picks, {
            title: `Save cube '${name}' to connections.json?`,
            placeHolder: status.found ? `Found ${status.path}` : 'No connections.json found nearby — choose where to save',
        });
        if (!pick || pick.action === 'skip') {
            return;
        }

        let targetPath: string | undefined;
        if (pick.action === 'existing') {
            targetPath = status.path;
        } else {
            const chosen = await vscode.window.showSaveDialog({
                title: 'Save cube to…',
                defaultUri: vscode.Uri.file(path.join(directory, 'connections.json')),
                filters: { JSON: ['json'] },
                saveLabel: 'Save cube',
            });
            if (!chosen) {
                return;
            }
            targetPath = chosen.fsPath;
        }

        try {
            const result = await client.request<SaveConfigResult>('clrkernel/connections/saveConfig', {
                languageId: 'dax',
                name,
                filePath: targetPath,
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage('Could not save cube: ' + (result.error ?? 'unknown error'));
                return;
            }
            void vscode.window.showInformationMessage(
                `Saved '${name}' to ${result.path} — it will load automatically next session.`,
            );
        } catch (e) {
            void vscode.window.showErrorMessage('Could not save cube: ' + errorText(e));
        }
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

function errorText(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
}
