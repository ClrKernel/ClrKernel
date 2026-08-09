import * as vscode from 'vscode';
import { ClrKernelController } from './controller';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

// Matches a leading "-- connections name" selector comment (valid T-SQL).
const CONNECTION_RE = /^\s*--\s*connections?\s*[:=]?\s*(\S+)/i;

interface SqlConnectionInfo {
    name: string;
    server?: string;
    database?: string;
    auth: string;
    user?: string;
    describe: string;
    needsSecret: boolean;
    secretRef: string;
    isDefault: boolean;
}

interface ListResult {
    defaultName?: string;
    connections: SqlConnectionInfo[];
}

interface AddResult {
    ok: boolean;
    name?: string;
    secretRef?: string;
    error?: string;
}

/**
 * The SQL connection experience: a status-bar button on every SQL cell showing
 * the connection it runs against, and a guided QuickPick to pick, add, or manage
 * connections — so users never have to memorize the #!sql-connect syntax.
 * Passwords are typed into a masked input and handed straight to the kernel,
 * which stores them in the OS credential store; they are never written to the
 * notebook.
 */
export class SqlConnectionUi {
    private readonly changeEmitter = new vscode.EventEmitter<void>();

    constructor(private readonly controller: ClrKernelController) { }

    register(context: vscode.ExtensionContext): void {
        context.subscriptions.push(
            vscode.notebooks.registerNotebookCellStatusBarItemProvider(NOTEBOOK_TYPE, this.statusBarProvider()),
            vscode.commands.registerCommand('clrkernel.sql.selectConnection', (cell?: vscode.NotebookCell) => this.selectConnection(cell)),
            vscode.commands.registerCommand('clrkernel.sql.addConnection', (cell?: vscode.NotebookCell) => this.addConnection(cell)),
            this.changeEmitter,
        );
    }

    private statusBarProvider(): vscode.NotebookCellStatusBarItemProvider {
        return {
            onDidChangeCellStatusBarItems: this.changeEmitter.event,
            provideCellStatusBarItems: (cell) => {
                if (cell.document.languageId !== 'sql') {
                    return [];
                }
                const name = currentConnection(cell.document.getText());
                const item = new vscode.NotebookCellStatusBarItem(
                    '$(database) ' + (name ?? 'Select connection'),
                    vscode.NotebookCellStatusBarAlignment.Right,
                );
                item.command = {
                    title: 'Select SQL connection',
                    command: 'clrkernel.sql.selectConnection',
                    arguments: [cell],
                };
                item.tooltip = 'Choose which SQL connection this cell runs against';
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
        const selected = editor.selection;
        return editor.notebook.cellAt(selected.start);
    }

    private async selectConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }

        let list: ListResult;
        try {
            const client = await this.controller.getClient(cell.notebook);
            list = await client.request<ListResult>('clrkernel/sql/listConnections', {});
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }

        type Pick = vscode.QuickPickItem & { conn?: string; action?: 'add' };
        const picks: Pick[] = list.connections.map((c) => ({
            label: '$(database) ' + c.name,
            description: c.describe + (c.isDefault ? '  •  default' : ''),
            conn: c.name,
        }));
        if (picks.length > 0) {
            picks.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
        }
        picks.push({ label: '$(add) Add connection…', action: 'add' });

        const pick = await vscode.window.showQuickPick(picks, {
            placeHolder: list.connections.length > 0
                ? 'Run this SQL cell against…'
                : 'No connections yet — add one',
        });
        if (!pick) {
            return;
        }
        if (pick.action === 'add') {
            await this.addConnection(cell);
            return;
        }
        if (pick.conn) {
            await applyConnection(cell, pick.conn);
            this.changeEmitter.fire();
        }
    }

    private async addConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }

        const name = await vscode.window.showInputBox({
            title: 'New SQL connection (1/5)',
            prompt: 'Connection name (used in cells, e.g. analytics)',
            validateInput: (v) => (/^\w[\w-]*$/.test(v) ? undefined : 'Use letters, digits, _ or -'),
        });
        if (!name) {
            return;
        }

        const server = await vscode.window.showInputBox({
            title: 'New SQL connection (2/5)',
            prompt: 'Server / host (e.g. sql-warehouse or tcp:host,1433)',
            ignoreFocusOut: true,
        });
        if (!server) {
            return;
        }

        const database = await vscode.window.showInputBox({
            title: 'New SQL connection (3/5)',
            prompt: 'Database (optional)',
            ignoreFocusOut: true,
        });
        if (database === undefined) {
            return;
        }

        const authPick = await vscode.window.showQuickPick(
            [
                { label: 'SQL login (username + password)', auth: 'sql', creds: true },
                { label: 'Integrated (Windows; Microsoft Entra default on macOS/Linux)', auth: 'integrated', creds: false },
                { label: 'Microsoft Entra — default (managed identity / az login)', auth: 'entra', creds: false },
                { label: 'Microsoft Entra — username + password', auth: 'entra-password', creds: true },
                { label: 'Microsoft Entra — interactive (browser sign-in)', auth: 'entra-interactive', creds: false },
            ],
            { title: 'New SQL connection (4/5)', placeHolder: 'Authentication' },
        );
        if (!authPick) {
            return;
        }

        let user: string | undefined;
        let secret: string | undefined;
        if (authPick.creds) {
            user = await vscode.window.showInputBox({
                title: 'New SQL connection (5/5)',
                prompt: 'Username',
                ignoreFocusOut: true,
            });
            if (!user) {
                return;
            }
            secret = await vscode.window.showInputBox({
                title: 'New SQL connection (5/5)',
                prompt: 'Password (stored in your OS credential store — never written to the notebook)',
                password: true,
                ignoreFocusOut: true,
            });
            if (secret === undefined) {
                return;
            }
        }

        const directive = buildConnectDirective({
            name,
            server,
            database: database.trim(),
            auth: authPick.auth,
            user,
        });

        try {
            const client = await this.controller.getClient(cell.notebook);
            const result = await client.request<AddResult>('clrkernel/sql/addConnection', {
                directive,
                secret: secret ?? '',
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage('Could not add connection: ' + (result.error ?? 'unknown error'));
                return;
            }
        } catch (e) {
            void vscode.window.showErrorMessage('Could not add connection: ' + errorText(e));
            return;
        }

        await applyConnection(cell, name);
        this.changeEmitter.fire();
        void vscode.window.showInformationMessage(
            `SQL connection '${name}' is ready.` + (secret ? ' Password saved to your OS credential store.' : ''),
        );
    }
}

/** Reads the connection named by a leading "-- connections name" line, if any. */
function currentConnection(text: string): string | undefined {
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

/** Inserts or replaces the leading "-- connections name" selector in a cell. */
async function applyConnection(cell: vscode.NotebookCell, name: string): Promise<void> {
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

function buildConnectDirective(opts: {
    name: string;
    server: string;
    database?: string;
    auth: string;
    user?: string;
}): string {
    const parts = ['#!sql-connect', '--name', quote(opts.name), '--server', quote(opts.server)];
    if (opts.database) {
        parts.push('--database', quote(opts.database));
    }
    parts.push('--auth', opts.auth);
    if (opts.user) {
        parts.push('--user', quote(opts.user));
    }
    return parts.join(' ');
}

function quote(value: string): string {
    return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

function errorText(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
}
