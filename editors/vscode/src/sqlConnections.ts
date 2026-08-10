import * as path from 'path';
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
            vscode.commands.registerCommand('clrkernel.sql.editConnection', (cell?: vscode.NotebookCell) => this.editConnection(cell)),
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

        // Bring in any saved connections.json entries so they appear in the list.
        await this.controller.ensureConnectionsConfigLoaded(cell.notebook);

        let list: ListResult;
        try {
            const client = await this.controller.getClient(cell.notebook);
            list = await client.request<ListResult>('clrkernel/sql/listConnections', {});
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }

        type Pick = vscode.QuickPickItem & { conn?: string; action?: 'add' | 'edit' };
        const picks: Pick[] = list.connections.map((c) => ({
            label: '$(database) ' + c.name,
            description: c.describe + (c.isDefault ? '  •  default' : ''),
            conn: c.name,
        }));
        if (picks.length > 0) {
            picks.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
        }
        picks.push({ label: '$(add) Add connection…', action: 'add' });
        if (list.connections.length > 0) {
            picks.push({ label: '$(edit) Edit connection…', action: 'edit' });
        }

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
        if (pick.action === 'edit') {
            await this.editConnection(cell);
            return;
        }
        if (pick.conn) {
            await applyConnection(cell, pick.conn);
            this.changeEmitter.fire();
        }
    }

    private async addConnection(cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (cell) {
            await this.runConnectionWizard(cell);
        }
    }

    /** Pick an existing connection and re-run the wizard pre-filled with its settings. */
    private async editConnection(cellArg?: vscode.NotebookCell): Promise<void> {
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
        if (list.connections.length === 0) {
            void vscode.window.showInformationMessage('There are no SQL connections to edit yet.');
            return;
        }

        // If more than one, ask which; if the cell already targets one, offer it first.
        let target = list.connections[0];
        if (list.connections.length > 1) {
            const current = currentConnection(cell.document.getText());
            const ordered = [...list.connections].sort((a, b) =>
                (a.name === current ? -1 : 0) - (b.name === current ? -1 : 0));
            const pick = await vscode.window.showQuickPick(
                ordered.map((c) => ({
                    label: '$(database) ' + c.name,
                    description: c.describe + (c.name === current ? '  •  this cell' : ''),
                    conn: c,
                })),
                { title: 'Edit SQL connection', placeHolder: 'Which connection?' },
            );
            if (!pick) {
                return;
            }
            target = pick.conn;
        }

        await this.runConnectionWizard(cell, target);
    }

    // Add (existing == null) and Edit (existing set) share this wizard. When editing,
    // the name is fixed, server/database/auth are pre-filled, and the password can be
    // left blank to keep the one already in the OS credential store.
    private async runConnectionWizard(cell: vscode.NotebookCell, existing?: SqlConnectionInfo): Promise<void> {
        const editing = existing !== undefined;
        const verb = editing ? 'Edit' : 'New';
        let step = 1;
        const steps = editing ? 5 : 6; // editing skips the name prompt
        const title = () => `${verb} SQL connection (${step++}/${steps})`;

        let name = existing?.name ?? '';
        if (!editing) {
            const entered = await vscode.window.showInputBox({
                title: title(),
                prompt: 'Connection name (used in cells, e.g. analytics)',
                validateInput: (v) => (/^\w[\w-]*$/.test(v) ? undefined : 'Use letters, digits, _ or -'),
            });
            if (!entered) {
                return;
            }
            name = entered;
        }

        const server = await vscode.window.showInputBox({
            title: title(),
            prompt: 'Server / host (e.g. sql-warehouse or tcp:host,1433)',
            value: existing?.server,
            ignoreFocusOut: true,
        });
        if (!server) {
            return;
        }

        const database = await vscode.window.showInputBox({
            title: title(),
            prompt: 'Database (optional)',
            value: existing?.database,
            ignoreFocusOut: true,
        });
        if (database === undefined) {
            return;
        }

        // Pre-select the current auth mode first when editing.
        const authOptions = [
            { label: 'SQL login (username + password)', auth: 'sql', creds: true },
            { label: 'Integrated (Windows; Microsoft Entra default on macOS/Linux)', auth: 'integrated', creds: false },
            { label: 'Microsoft Entra — default (managed identity / az login)', auth: 'entra', creds: false },
            { label: 'Microsoft Entra — username + password', auth: 'entra-password', creds: true },
            { label: 'Microsoft Entra — interactive (browser sign-in)', auth: 'entra-interactive', creds: false },
        ];
        const currentAuth = existing ? authFromMode(existing.auth) : undefined;
        const orderedAuth = currentAuth
            ? [...authOptions].sort((a, b) => (a.auth === currentAuth ? -1 : 0) - (b.auth === currentAuth ? -1 : 0))
                .map((o) => (o.auth === currentAuth ? { ...o, description: 'current' } : o))
            : authOptions;
        const authPick = await vscode.window.showQuickPick(orderedAuth, {
            title: title(),
            placeHolder: 'Authentication',
        });
        if (!authPick) {
            return;
        }

        // Encryption / certificate. SQL Server defaults to Encrypt=true with certificate
        // validation; local / on-prem servers usually have a self-signed cert, which fails
        // with "the certificate chain was issued by an authority that is not trusted" unless
        // the certificate is trusted.
        const encPick = await vscode.window.showQuickPick(
            [
                { label: 'Encrypt, trust the server certificate', description: 'self-signed / local or on-prem SQL Server', encrypt: true, trustCert: true },
                { label: 'Encrypt, validate the certificate (default)', description: 'Azure SQL or a trusted CA certificate', encrypt: true, trustCert: false },
                { label: 'Do not encrypt', description: 'legacy servers only', encrypt: false, trustCert: false },
            ],
            { title: title(), placeHolder: 'Encryption' },
        );
        if (!encPick) {
            return;
        }

        let user: string | undefined;
        let secret: string | undefined;
        if (authPick.creds) {
            user = await vscode.window.showInputBox({
                title: title(),
                prompt: 'Username',
                value: existing?.user,
                ignoreFocusOut: true,
            });
            if (!user) {
                return;
            }
            secret = await vscode.window.showInputBox({
                title: title(),
                prompt: editing
                    ? 'Password — leave blank to keep the current one (stored in your OS credential store)'
                    : 'Password (stored in your OS credential store — never written to the notebook)',
                password: true,
                ignoreFocusOut: true,
            });
            if (secret === undefined) {
                return; // cancelled
            }
        }

        const directive = buildConnectDirective({
            name,
            server,
            database: database.trim(),
            auth: authPick.auth,
            user,
            encrypt: encPick.encrypt,
            trustCert: encPick.trustCert,
        });

        try {
            const client = await this.controller.getClient(cell.notebook);
            // Empty secret leaves the existing stored password untouched (the kernel only
            // writes the secret when a non-empty one is sent), so blank on edit keeps it.
            const result = await client.request<AddResult>('clrkernel/sql/addConnection', {
                directive,
                secret: secret ?? '',
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage(`Could not ${editing ? 'update' : 'add'} connection: ` + (result.error ?? 'unknown error'));
                return;
            }
        } catch (e) {
            void vscode.window.showErrorMessage(`Could not ${editing ? 'update' : 'add'} connection: ` + errorText(e));
            return;
        }

        if (!editing) {
            await applyConnection(cell, name);
        }
        this.changeEmitter.fire();
        const savedPw = secret ? ' Password saved to your OS credential store.' : '';
        void vscode.window.showInformationMessage(
            editing ? `SQL connection '${name}' updated.${savedPw}` : `SQL connection '${name}' is ready.${savedPw}`,
        );

        await this.promptSaveToConfig(cell, name);
    }

    // Offers to persist the just-added/edited connection to a connections.json so it
    // reloads automatically in future sessions. Shows whether one was found nearby and
    // always lets the user confirm that file or choose another (the password is never
    // written — only a secret reference).
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
            status = await client.request<ConfigStatusResult>('clrkernel/sql/configStatus', { directory });
        } catch {
            return; // couldn't check — don't nag
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
            title: `Save '${name}' to connections.json?`,
            placeHolder: status.found
                ? `Found ${status.path}`
                : 'No connections.json found nearby — choose where to save',
        });
        if (!pick || pick.action === 'skip') {
            return;
        }

        let targetPath: string | undefined;
        if (pick.action === 'existing') {
            targetPath = status.path;
        } else {
            const chosen = await vscode.window.showSaveDialog({
                title: 'Save connection to…',
                defaultUri: vscode.Uri.file(path.join(directory, 'connections.json')),
                filters: { JSON: ['json'] },
                saveLabel: 'Save connection',
            });
            if (!chosen) {
                return;
            }
            targetPath = chosen.fsPath;
        }

        try {
            const result = await client.request<SaveConfigResult>('clrkernel/sql/saveConnection', {
                name,
                filePath: targetPath,
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage('Could not save connection: ' + (result.error ?? 'unknown error'));
                return;
            }
            void vscode.window.showInformationMessage(
                `Saved '${name}' to ${result.path} — it will load automatically next session.`,
            );
        } catch (e) {
            void vscode.window.showErrorMessage('Could not save connection: ' + errorText(e));
        }
    }
}

/** Maps a SqlAuthMode enum name (from listConnections) to the wizard's auth value. */
function authFromMode(mode: string): string | undefined {
    switch (mode) {
        case 'SqlPassword': return 'sql';
        case 'Integrated': return 'integrated';
        case 'AzureAdDefault': return 'entra';
        case 'AzureAdPassword': return 'entra-password';
        case 'AzureAdInteractive': return 'entra-interactive';
        default: return undefined;
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
    encrypt?: boolean;
    trustCert?: boolean;
}): string {
    const parts = ['#!sql-connect', '--name', quote(opts.name), '--server', quote(opts.server)];
    if (opts.database) {
        parts.push('--database', quote(opts.database));
    }
    parts.push('--auth', opts.auth);
    if (opts.user) {
        parts.push('--user', quote(opts.user));
    }
    if (opts.encrypt === false) {
        parts.push('--encrypt', 'false');
    }
    if (opts.trustCert) {
        parts.push('--trust-cert');
    }
    return parts.join(' ');
}

function quote(value: string): string {
    return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

function errorText(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
}
