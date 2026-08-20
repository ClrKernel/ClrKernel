import * as path from 'path';
import * as vscode from 'vscode';
import { composeConnectDirective, ConnectionProviderDescriptor, ConnectionSetting } from './connectionDirective';
import { ClrKernelController } from './controller';
import { currentLanguages, LanguageDescriptor } from './languages';

const NOTEBOOK_TYPE = 'clrkernel-markdown';

// The leading selector comment a cell targets a connection with. One superset
// pattern covers every language's comment dialect (SQL's --, DAX's -- and //,
// and the cube keyword).
const CONNECTION_RE = /^\s*(?:--|\/\/)\s*(?:connections?|cube)\s*[:=]?\s*(\S+)/i;

interface ConnectionInfo {
    name: string;
    server?: string;
    database?: string;
    auth?: string;
    user?: string;
    describe: string;
    needsSecret?: boolean;
    secretRef?: string;
    isDefault: boolean;
}

interface ListResult { defaultName?: string; connections: ConnectionInfo[] }
interface AddResult { ok: boolean; name?: string; error?: string }
interface DescribeResult { ok: boolean; providers: ConnectionProviderDescriptor[]; error?: string }
interface ConfigStatusResult { ok: boolean; found: boolean; path?: string; names: string[]; error?: string }
interface SaveConfigResult { ok: boolean; path?: string; error?: string }

/**
 * The connection experience for every language that has one: a status-bar
 * button on each cell showing the connection it runs against, and a wizard
 * rendered from the kernel's ConnectionProviderDescriptor schema — fields, auth
 * modes, one-of groups, defaults — so a new provider gets a working connection
 * UI with zero extension changes. Passwords go into a masked input and ride the
 * RPC's `secret` parameter straight into the OS credential store; they are
 * never written to the notebook or the directive line.
 */
export class ConnectionUi {
    private readonly changeEmitter = new vscode.EventEmitter<void>();

    constructor(private readonly controller: ClrKernelController) { }

    register(context: vscode.ExtensionContext): void {
        const forLanguage = (languageId: string, action: (id: string, cell?: vscode.NotebookCell) => Promise<void>) =>
            (cell?: vscode.NotebookCell) => action.call(this, languageId, cell);
        context.subscriptions.push(
            vscode.notebooks.registerNotebookCellStatusBarItemProvider(NOTEBOOK_TYPE, this.statusBarProvider()),
            // Generic commands drive the status-bar items for ANY language;
            // the per-language ids below keep the palette entries (and any
            // keybindings) that shipped with earlier versions working.
            vscode.commands.registerCommand('clrkernel.connections.select',
                (languageId: string, cell?: vscode.NotebookCell) => this.select(languageId, cell)),
            vscode.commands.registerCommand('clrkernel.sql.selectConnection', forLanguage('sql', this.select)),
            vscode.commands.registerCommand('clrkernel.sql.addConnection', forLanguage('sql', this.add)),
            vscode.commands.registerCommand('clrkernel.sql.editConnection', forLanguage('sql', this.edit)),
            vscode.commands.registerCommand('clrkernel.dax.selectConnection', forLanguage('dax', this.select)),
            vscode.commands.registerCommand('clrkernel.dax.addConnection', forLanguage('dax', this.add)),
            vscode.commands.registerCommand('clrkernel.dax.editConnection', forLanguage('dax', this.edit)),
            this.changeEmitter,
        );
    }

    private statusBarProvider(): vscode.NotebookCellStatusBarItemProvider {
        return {
            onDidChangeCellStatusBarItems: this.changeEmitter.event,
            provideCellStatusBarItems: (cell) => {
                const language = currentLanguages().find(
                    (l) => l.id === cell.document.languageId && l.hasConnections);
                if (!language) {
                    return [];
                }
                const name = currentConnection(cell.document.getText());
                const item = new vscode.NotebookCellStatusBarItem(
                    '$(database) ' + (name ?? 'Select connection'),
                    vscode.NotebookCellStatusBarAlignment.Right,
                );
                item.command = {
                    title: 'Select connection',
                    command: 'clrkernel.connections.select',
                    arguments: [language.id, cell],
                };
                item.tooltip = `Choose which connection this ${language.displayName} cell runs against`;
                return [item];
            },
        };
    }

    private resolveCell(cell?: vscode.NotebookCell): vscode.NotebookCell | undefined {
        if (cell) {
            return cell;
        }
        const editor = vscode.window.activeNotebookEditor;
        return editor ? editor.notebook.cellAt(editor.selection.start) : undefined;
    }

    private async list(languageId: string, cell: vscode.NotebookCell): Promise<ListResult | undefined> {
        try {
            const client = await this.controller.getClient(cell.notebook);
            return await client.request<ListResult>('clrkernel/connections/list',
                { languageId, notebookUri: cell.notebook.uri.toString() });
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return undefined;
        }
    }

    private async select(languageId: string, cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }
        // Bring in any saved connections.json entries so they appear in the list.
        await this.controller.ensureConnectionsConfigLoaded(cell.notebook);
        const list = await this.list(languageId, cell);
        if (!list) {
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
            placeHolder: list.connections.length > 0 ? 'Run this cell against…' : 'No connections yet — add one',
        });
        if (!pick) {
            return;
        }
        if (pick.action === 'add') {
            await this.add(languageId, cell);
        } else if (pick.action === 'edit') {
            await this.edit(languageId, cell);
        } else if (pick.conn) {
            await applyConnection(cell, pick.conn);
            this.changeEmitter.fire();
        }
    }

    private async add(languageId: string, cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (cell) {
            await this.runWizard(languageId, cell);
        }
    }

    /** Pick an existing connection and re-run the wizard pre-filled with its settings. */
    private async edit(languageId: string, cellArg?: vscode.NotebookCell): Promise<void> {
        const cell = this.resolveCell(cellArg);
        if (!cell) {
            return;
        }
        const list = await this.list(languageId, cell);
        if (!list) {
            return;
        }
        if (list.connections.length === 0) {
            void vscode.window.showInformationMessage('There are no connections to edit yet.');
            return;
        }

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
                { title: 'Edit connection', placeHolder: 'Which connection?' },
            );
            if (!pick) {
                return;
            }
            target = pick.conn;
        }
        await this.runWizard(languageId, cell, target);
    }

    /**
     * The schema-driven wizard, shared by Add (existing == null) and Edit.
     * Renders one prompt per descriptor setting: one-of groups become a mode
     * pick first, enums and bools become QuickPicks, everything else an input.
     * When editing, the name is fixed, matching fields are pre-filled, and a
     * blank password keeps the one already in the OS credential store.
     */
    private async runWizard(languageId: string, cell: vscode.NotebookCell, existing?: ConnectionInfo): Promise<void> {
        const language = currentLanguages().find((l) => l.id === languageId);
        let providers: ConnectionProviderDescriptor[];
        try {
            const client = await this.controller.getClient(cell.notebook);
            const described = await client.request<DescribeResult>('clrkernel/connections/describe',
                { languageId, notebookUri: cell.notebook.uri.toString() });
            providers = (described.providers ?? []).filter((p) => p.connectSelector);
        } catch (e) {
            void vscode.window.showErrorMessage('Could not reach the ClrKernel server: ' + errorText(e));
            return;
        }
        if (providers.length === 0) {
            void vscode.window.showInformationMessage(`No connection providers are available for ${languageId}.`);
            return;
        }

        let provider = providers[0];
        if (providers.length > 1) {
            const pick = await vscode.window.showQuickPick(
                providers.map((p) => ({ label: p.displayName, description: p.description, provider: p })),
                { title: 'Connection type', placeHolder: 'Which kind of connection?' },
            );
            if (!pick) {
                return;
            }
            provider = pick.provider;
        }

        const editing = existing !== undefined;
        const title = `${editing ? 'Edit' : 'New'} ${provider.displayName} connection`;
        const directive = language?.directives?.find((d) => d.selector === provider.connectSelector);
        const values: Record<string, string | boolean | undefined> = {};
        const prefill = (setting: ConnectionSetting): string | undefined =>
            existing?.[setting.name as 'server' | 'database' | 'user'];

        // The connection name comes first and is fixed when editing.
        const nameSetting = provider.settings.find((s) => s.name === 'name');
        if (nameSetting) {
            if (editing) {
                values.name = existing.name;
            } else {
                const entered = await vscode.window.showInputBox({
                    title,
                    prompt: 'Connection name (used in cells, e.g. analytics)',
                    validateInput: (v) => (/^\w[\w-]*$/.test(v) ? undefined : 'Use letters, digits, _ or -'),
                });
                if (!entered) {
                    return;
                }
                values.name = entered;
            }
        }

        // One-of groups: pick the mode, then prompt only the chosen member — which
        // is required by virtue of having been chosen (a blank "server" after
        // picking "connect by server" makes no sense).
        const grouped = provider.settings.filter((s) => s.oneOfGroup && !s.runtimeOnly);
        for (const group of [...new Set(grouped.map((s) => s.oneOfGroup))]) {
            const members = grouped.filter((s) => s.oneOfGroup === group);
            const pick = await vscode.window.showQuickPick(
                members.map((s) => ({ label: s.displayName ?? s.name, description: s.description, setting: s })),
                { title, placeHolder: `Connect by…` },
            );
            if (!pick) {
                return;
            }
            if (!(await this.promptSetting(title, pick.setting, values, prefill(pick.setting), true))) {
                return;
            }
        }

        // Which auth values need a user + secret, per the descriptor. Once the
        // auth enum is answered, credentials are required for those values and
        // skipped entirely for the rest (integrated / Entra sign-in).
        const credsEnum = provider.settings.find((s) => s.kind === 'enum' && s.credentialValues?.length);
        const credsNeeded = (): boolean | undefined => {
            if (!credsEnum) {
                return undefined;
            }
            const chosen = values[credsEnum.name];
            return typeof chosen === 'string' ? credsEnum.credentialValues!.includes(chosen) : undefined;
        };

        // Everything else, in descriptor order. Secrets come last.
        const rest = provider.settings.filter((s) =>
            s.name !== 'name' && !s.oneOfGroup && !s.runtimeOnly && s.kind !== 'secretRef' && s.kind !== 'keyValueBag');
        for (const setting of rest) {
            if (setting.name === 'user' && credsNeeded() === false) {
                continue; // the chosen auth mode carries its own identity
            }
            // A setting another one declares it requires: required once that owner
            // was given (workspace → model), skipped when the owner is an unchosen
            // group member (no model prompt on the plain-server path).
            const owner = provider.settings.find((s) => s.requires?.includes(setting.name));
            if (owner && values[owner.name] === undefined && owner.oneOfGroup) {
                continue;
            }
            const forceRequired = (setting.name === 'user' && credsNeeded() === true) ||
                (owner !== undefined && values[owner.name] !== undefined);
            if (!(await this.promptSetting(title, setting, values, prefill(setting), forceRequired))) {
                return;
            }
        }

        let secret: string | undefined;
        const secretSetting = provider.settings.find((s) => s.kind === 'secretRef' && !s.runtimeOnly);
        const userWanted = credsNeeded()
            ?? (!provider.settings.some((s) => s.name === 'user') || !!values.user);
        if (secretSetting && userWanted) {
            secret = await vscode.window.showInputBox({
                title,
                prompt: editing
                    ? `${secretSetting.displayName ?? 'Password'} — leave blank to keep the current one (stored in your OS credential store)`
                    : `${secretSetting.displayName ?? 'Password'} (stored in your OS credential store — never written to the notebook)`,
                password: true,
                ignoreFocusOut: true,
            });
            if (secret === undefined) {
                return; // cancelled
            }
        }

        const line = composeConnectDirective(provider, directive, values);
        const name = String(values.name ?? '');
        try {
            const client = await this.controller.getClient(cell.notebook);
            // Empty secret leaves an existing stored password untouched (the kernel
            // only writes the secret when a non-empty one is sent).
            const result = await client.request<AddResult>('clrkernel/connections/add', {
                languageId,
                notebookUri: cell.notebook.uri.toString(),
                directive: line,
                secret: secret ?? '',
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage(
                    `Could not ${editing ? 'update' : 'add'} connection: ` + (result.error ?? 'unknown error'));
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
            editing ? `Connection '${name}' updated.${savedPw}` : `Connection '${name}' is ready.${savedPw}`);

        if (language?.configBacked) {
            await this.promptSaveToConfig(languageId, cell, name);
        }
    }

    /** One prompt for one setting. Returns false when the user cancelled the wizard. */
    private async promptSetting(
        title: string,
        setting: ConnectionSetting,
        values: Record<string, string | boolean | undefined>,
        prefill?: string,
        forceRequired = false,
    ): Promise<boolean> {
        const required = setting.required || forceRequired;
        const label = setting.displayName ?? setting.name;
        if (setting.kind === 'enum' && setting.enumValues?.length) {
            const ordered = setting.default
                ? [...setting.enumValues].sort((a, b) => (a === setting.default ? -1 : 0) - (b === setting.default ? -1 : 0))
                : setting.enumValues;
            const pick = await vscode.window.showQuickPick(
                ordered.map((v) => ({ label: v, description: v === setting.default ? 'default' : undefined })),
                { title, placeHolder: label + (setting.description ? ` — ${setting.description}` : '') },
            );
            if (!pick) {
                return false;
            }
            values[setting.name] = pick.label;
            return true;
        }
        if (setting.kind === 'bool') {
            const pick = await vscode.window.showQuickPick(
                [
                    { label: 'Yes', value: 'true', description: setting.default === 'true' ? 'default' : undefined },
                    { label: 'No', value: 'false', description: setting.default !== 'true' ? 'default' : undefined },
                ].sort((a, b) => (a.description ? -1 : 0) - (b.description ? -1 : 0)),
                { title, placeHolder: label + (setting.description ? ` — ${setting.description}` : '') },
            );
            if (!pick) {
                return false;
            }
            values[setting.name] = pick.value;
            return true;
        }
        const entered = await vscode.window.showInputBox({
            title,
            prompt: label + (required ? '' : ' (optional)') + (setting.description ? ` — ${setting.description}` : ''),
            value: prefill ?? setting.default ?? undefined,
            ignoreFocusOut: true,
            validateInput: setting.kind === 'int'
                ? (v) => (v === '' || /^\d+$/.test(v) ? undefined : 'Enter a number')
                : required
                    ? (v) => (v.trim().length > 0 ? undefined : `${label} is required`)
                    : undefined,
        });
        if (entered === undefined || (required && entered.trim() === '')) {
            return false;
        }
        if (entered !== '') {
            values[setting.name] = entered;
        }
        return true;
    }

    // Offers to persist the just-added/edited connection to a connections.json so it
    // reloads automatically in future sessions (the password is never written — only
    // a secret reference).
    private async promptSaveToConfig(languageId: string, cell: vscode.NotebookCell, name: string): Promise<void> {
        let client;
        try {
            client = await this.controller.getClient(cell.notebook);
        } catch {
            return; // server not reachable — nothing to offer
        }

        const directory = path.dirname(cell.notebook.uri.fsPath);
        let status: ConfigStatusResult;
        try {
            status = await client.request<ConfigStatusResult>('clrkernel/connections/configStatus',
                { languageId, notebookUri: cell.notebook.uri.toString(), directory });
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
            const result = await client.request<SaveConfigResult>('clrkernel/connections/saveConfig', {
                languageId,
                notebookUri: cell.notebook.uri.toString(),
                name,
                filePath: targetPath,
            });
            if (!result.ok) {
                void vscode.window.showErrorMessage('Could not save connection: ' + (result.error ?? 'unknown error'));
                return;
            }
            void vscode.window.showInformationMessage(
                `Saved '${name}' to ${result.path} — it will load automatically next session.`);
        } catch (e) {
            void vscode.window.showErrorMessage('Could not save connection: ' + errorText(e));
        }
    }
}

/** Reads the connection named by a leading selector comment, if any. */
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

function errorText(e: unknown): string {
    return e instanceof Error ? e.message : String(e);
}
