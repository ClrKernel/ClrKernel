import * as path from 'path';
import * as vscode from 'vscode';
import { compareKernelVersion, kernelVersionWarning } from './kernelVersion';
import { toOutputItemData } from './outputItems';
import { DisplayNotification, ServerClient } from './serverClient';
import { SingleFlight } from './singleFlight';
import { offerServerInstall, resolveGlobalToolPath } from './serverSetup';

/**
 * NotebookController that executes C# cells through ClrKernel.Core.ExtensionServer. One
 * server process per VS Code window; REPL state is shared across notebooks in
 * that window (matching how a Jupyter kernel session behaves).
 */
export class ClrKernelController {
    private readonly controller: vscode.NotebookController;
    private readonly output: vscode.OutputChannel;
    // One version notice per window; a restart is what re-arms it.
    private warnedKernelVersion = false;
    private client: ServerClient | undefined;
    // One server per window, even when several notebooks ask at once — see SingleFlight.
    private readonly starting = new SingleFlight<ServerClient>();

    // Routing tables for streaming output.
    private readonly activeExecutions = new Map<string, vscode.NotebookCellExecution>();
    private readonly displayOutputs = new Map<string, { execution: vscode.NotebookCellExecution; output: vscode.NotebookCellOutput }>();

    // Notebooks whose connections.json has already been auto-loaded this session.
    private readonly loadedConfigNotebooks = new Set<string>();

    constructor(notebookType: string) {
        this.output = vscode.window.createOutputChannel('ClrKernel');
        this.controller = vscode.notebooks.createNotebookController('clrkernel-csharp', notebookType, 'ClrKernel');
        // C# cells run as script; http cells run as .http requests; mermaid
        // cells render as diagrams; powershell cells run in the runspace.
        // C# cells use the 'csharp-script' language id (shown as "C#", keeps other C#
        // tooling from attaching). Plain 'csharp' is intentionally NOT listed: it would
        // add a second "C#" entry to the cell language picker, and the serializer already
        // maps every ```csharp fence to 'csharp-script' on load, so no cell is 'csharp'.
        this.controller.supportedLanguages = ['csharp-script', 'http', 'mermaid', 'powershell', 'sql', 'dax'];
        this.controller.supportsExecutionOrder = true;
        this.controller.executeHandler = (cells) => this.executeCells(cells);
    }

    private executionOrder = 0;

    private async ensureClient(notebook: vscode.NotebookDocument): Promise<ServerClient> {
        if (this.client?.running) {
            return this.client;
        }
        // Restoring a window opens every notebook at once, and a cell run can race the connection
        // button. Without this each of them starts its own server and all but the last are leaked.
        return this.starting.run(() => this.startForNotebook(notebook));
    }

    private async startForNotebook(notebook: vscode.NotebookDocument): Promise<ServerClient> {
        if (this.client?.running) {
            return this.client;
        }

        const configuration = vscode.workspace.getConfiguration('clrkernel');
        const configuredCommand = configuration.get<string>('server.command', 'clrkernel');
        const args = configuration.get<string[]>('server.args', ['lsp']);
        const cwd = path.dirname(notebook.uri.fsPath);
        const usingDefault = configuredCommand === 'clrkernel' && args.length === 1 && args[0] === 'lsp';
        const log = (message: string) => this.output.appendLine(message);

        // When relying on the global tool, prefer its absolute path if present
        // (dodges the PATH gap after a fresh `dotnet tool install -g`).
        const command = usingDefault ? (resolveGlobalToolPath() ?? configuredCommand) : configuredCommand;

        try {
            return await this.startClient(command, args, cwd);
        } catch (startError) {
            // A misconfigured custom command is the user's to fix — just report.
            if (!usingDefault) {
                this.reportStartError(startError);
                throw startError;
            }
            // Default path: the tool is probably just not installed. Offer to fix.
            const installedCommand = await offerServerInstall(log);
            if (!installedCommand) {
                this.reportStartError(startError);
                throw startError;
            }
            try {
                return await this.startClient(installedCommand, args, cwd);
            } catch (retryError) {
                this.reportStartError(retryError);
                throw retryError;
            }
        }
    }

    private async startClient(command: string, args: string[], cwd: string): Promise<ServerClient> {
        const client = new ServerClient(command, args, cwd, this.output);
        await client.start();
        client.onDisplay((note) => this.onDisplay(note, false));
        client.onUpdateDisplay((note) => this.onDisplay(note, true));
        this.client = client;
        this.warnOnKernelMismatch(client);
        return client;
    }

    /**
     * Tells the user once per session when the installed kernel isn't the line this extension
     * speaks. Not fatal — cells still execute; it's the connection UI that stops working — so
     * this warns rather than refusing to start.
     */
    private warnOnKernelMismatch(client: ServerClient): void {
        if (this.warnedKernelVersion) {
            return;
        }
        const version = client.kernelVersion;
        const warning = kernelVersionWarning(compareKernelVersion(version), version);
        if (!warning) {
            return;
        }
        this.warnedKernelVersion = true;
        this.output.appendLine(warning);
        void vscode.window.showWarningMessage(warning, 'Show Output').then((pick) => {
            if (pick === 'Show Output') {
                this.output.show(true);
            }
        });
    }

    private reportStartError(error: unknown): void {
        const message = error instanceof Error ? error.message : String(error);
        this.output.appendLine(message);
        this.output.show(true);
    }

    private onDisplay(note: DisplayNotification, isUpdate: boolean): void {
        const items = toOutputItems(note.data);
        const displayId = note.transient?.display_id;

        if (isUpdate && displayId) {
            const target = this.displayOutputs.get(displayId);
            if (target) {
                void target.execution.replaceOutputItems(items, target.output);
                return;
            }
        }

        const execution = this.activeExecutions.get(note.cellId);
        if (!execution) {
            return; // output for a cell that already finished; drop for now
        }
        const output = new vscode.NotebookCellOutput(items);
        void execution.appendOutput(output);
        if (displayId) {
            this.displayOutputs.set(displayId, { execution, output });
        }
    }

    private async executeCells(cells: vscode.NotebookCell[]): Promise<void> {
        // Stop at the first failing cell by default (matches the headless runner and
        // Jupyter's nbconvert). Set clrkernel.stopOnCellError=false to run every
        // selected cell regardless of failures.
        const stopOnError = vscode.workspace.getConfiguration('clrkernel').get<boolean>('stopOnCellError', true);
        if (cells.length > 0) {
            await this.ensureConnectionsConfigLoaded(cells[0].notebook);
        }
        for (const cell of cells) {
            const ok = await this.executeCell(cell);
            if (!ok && stopOnError) {
                break;
            }
        }
    }

    private async executeCell(cell: vscode.NotebookCell): Promise<boolean> {
        const execution = this.controller.createNotebookCellExecution(cell);
        execution.executionOrder = ++this.executionOrder;
        execution.start(Date.now());
        void execution.clearOutput();

        const cellId = cell.document.uri.toString();
        this.activeExecutions.set(cellId, execution);

        try {
            const client = await this.ensureClient(cell.notebook);
            const result = await client.execute(cellId, this.cellCode(cell));

            if (result.status === 'ok') {
                if (result.data && Object.keys(result.data).length > 0) {
                    void execution.appendOutput(new vscode.NotebookCellOutput(toOutputItems(result.data)));
                }
                execution.end(true, Date.now());
                return true;
            }
            const error = new Error(result.error?.message ?? 'execution failed');
            error.name = result.error?.name ?? 'Error';
            // .NET stack traces are frames only — they don't include the "Name: message"
            // line JS stacks start with. VS Code renders error.stack, so without prepending
            // the message the cell shows a bare stack with no reason. Only set a stack when
            // we have one to prepend to; otherwise let VS Code show the message itself.
            const netStack = result.error?.stack;
            error.stack = netStack ? `${error.name}: ${error.message}\n${netStack}` : undefined;
            void execution.appendOutput(new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error)]));
            execution.end(false, Date.now());
            return false;
        } catch (e) {
            const error = e instanceof Error ? e : new Error(String(e));
            void execution.appendOutput(new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error)]));
            execution.end(false, Date.now());
            return false;
        } finally {
            this.activeExecutions.delete(cellId);
        }
    }

    /**
     * The code to send for a cell. HTTP cells are prefixed with `#!http`,
     * Mermaid cells with `#!mermaid`, and PowerShell cells with `#!pwsh`, so the
     * engine routes them (unless the user already typed the selector). C# cells
     * are sent verbatim.
     */
    private cellCode(cell: vscode.NotebookCell): string {
        const text = cell.document.getText();
        if (cell.document.languageId === 'http' && !/^\s*#!http\b/i.test(text)) {
            return '#!http\n' + text;
        }
        if (cell.document.languageId === 'mermaid' && !/^\s*#!mermaid\b/i.test(text)) {
            return '#!mermaid\n' + text;
        }
        if (cell.document.languageId === 'powershell' && !/^\s*#!(pwsh|powershell)\b/i.test(text)) {
            return '#!pwsh\n' + text;
        }
        if (cell.document.languageId === 'sql' && !/^\s*#!sql\b/i.test(text)) {
            return '#!sql\n' + text;
        }
        if (cell.document.languageId === 'dax' && !/^\s*#!dax\b/i.test(text)) {
            return '#!dax\n' + text;
        }
        return text;
    }

    /**
     * Ensures the server is running for the given notebook and returns the client,
     * so the SQL connection UI can issue clrkernel/connections/* requests over the same
     * process that runs cells (and therefore shares the connection registry).
     */
    async getClient(notebook: vscode.NotebookDocument): Promise<ServerClient> {
        return this.ensureClient(notebook);
    }

    /**
     * Registers saved connections from a connections.json at/above the notebook's folder
     * into the session, once per notebook, so they resolve when a cell runs without the
     * user re-adding them. Both kinds are loaded — SqlServer and AnalysisServices entries
     * live in the same file, told apart by $type. Best-effort: never blocks execution.
     */
    async ensureConnectionsConfigLoaded(notebook: vscode.NotebookDocument): Promise<void> {
        const key = notebook.uri.toString();
        if (this.loadedConfigNotebooks.has(key)) {
            return;
        }
        this.loadedConfigNotebooks.add(key);
        try {
            const client = await this.ensureClient(notebook);
            const directory = path.dirname(notebook.uri.fsPath);
            // Each language takes only its own $type; a language with nothing saved is a no-op.
            for (const languageId of ['sql', 'dax']) {
                await client.request('clrkernel/connections/loadConfig', { languageId, directory });
            }
        } catch (error) {
            // A missing/unreadable config or an unstarted server must not block the run.
            this.loadedConfigNotebooks.delete(key); // allow a later retry
            this.output.appendLine(
                'connections.json auto-load skipped: ' + (error instanceof Error ? error.message : String(error)));
        }
    }

    /**
     * Stops the server and drops the session, so the next run starts a fresh one.
     *
     * A REPL keeps every variable, open connection and PowerShell runspace from every cell that
     * has run in this window, which is exactly what you want until it isn't — a wedged connection,
     * a variable you'd rather forget, a module you want reloaded. There is no way to reach that
     * from the notebook UI otherwise; the process only ends when the window closes.
     *
     * Deliberately does not start a replacement. The next cell run does that, which keeps restart
     * cheap for someone who is closing up rather than carrying on.
     */
    async restart(): Promise<void> {
        const client = this.client;
        this.client = undefined;
        // Session-scoped state goes with the session: connection config is auto-loaded once per
        // notebook, and a stale entry here would skip the reload against the new process.
        this.loadedConfigNotebooks.clear();
        this.displayOutputs.clear();
        this.warnedKernelVersion = false;

        if (client) {
            try {
                await client.dispose();
            } catch (e) {
                this.output.appendLine('error stopping the kernel: ' + String(e));
            }
        }
        this.output.appendLine('kernel stopped; the next cell run starts a fresh one');
    }

    dispose(): void {
        void this.client?.dispose();
        this.controller.dispose();
        this.output.dispose();
    }
}

function toOutputItems(data: Record<string, unknown>): vscode.NotebookCellOutputItem[] {
    return toOutputItemData(data).map(item =>
        item.bytes
            ? new vscode.NotebookCellOutputItem(item.bytes, item.mime)
            : vscode.NotebookCellOutputItem.text(item.text ?? '', item.mime));
}
