import * as path from 'path';
import * as vscode from 'vscode';
import { DisplayNotification, ServerClient } from './serverClient';

/**
 * NotebookController that executes C# cells through ClrKernel.Server. One
 * server process per VS Code window; REPL state is shared across notebooks in
 * that window (matching how a Jupyter kernel session behaves).
 */
export class ClrKernelController {
    private readonly controller: vscode.NotebookController;
    private readonly output: vscode.OutputChannel;
    private client: ServerClient | undefined;

    // Routing tables for streaming output.
    private readonly activeExecutions = new Map<string, vscode.NotebookCellExecution>();
    private readonly displayOutputs = new Map<string, { execution: vscode.NotebookCellExecution; output: vscode.NotebookCellOutput }>();

    constructor(notebookType: string) {
        this.output = vscode.window.createOutputChannel('ClrKernel');
        this.controller = vscode.notebooks.createNotebookController('clrkernel-csharp', notebookType, 'ClrKernel C#');
        this.controller.supportedLanguages = ['csharp'];
        this.controller.supportsExecutionOrder = true;
        this.controller.executeHandler = (cells) => this.executeCells(cells);
    }

    private executionOrder = 0;

    private async ensureClient(notebook: vscode.NotebookDocument): Promise<ServerClient> {
        if (this.client?.running) {
            return this.client;
        }

        const configuration = vscode.workspace.getConfiguration('clrkernel');
        const command = configuration.get<string>('server.command', 'clrkernel-server');
        const args = configuration.get<string[]>('server.args', []);
        const cwd = path.dirname(notebook.uri.fsPath);

        const client = new ServerClient(command, args, cwd, (message) => this.output.appendLine(message));
        try {
            await client.start();
        } catch (e) {
            const message = e instanceof Error ? e.message : String(e);
            void vscode.window.showErrorMessage(message);
            this.output.appendLine(message);
            this.output.show(true);
            throw e;
        }

        client.onDisplay((note) => this.onDisplay(note, false));
        client.onUpdateDisplay((note) => this.onDisplay(note, true));

        this.client = client;
        return client;
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
        for (const cell of cells) {
            await this.executeCell(cell);
        }
    }

    private async executeCell(cell: vscode.NotebookCell): Promise<void> {
        const execution = this.controller.createNotebookCellExecution(cell);
        execution.executionOrder = ++this.executionOrder;
        execution.start(Date.now());
        void execution.clearOutput();

        const cellId = cell.document.uri.toString();
        this.activeExecutions.set(cellId, execution);

        try {
            const client = await this.ensureClient(cell.notebook);
            const result = await client.execute(cellId, cell.document.getText());

            if (result.status === 'ok') {
                if (result.data && Object.keys(result.data).length > 0) {
                    void execution.appendOutput(new vscode.NotebookCellOutput(toOutputItems(result.data)));
                }
                execution.end(true, Date.now());
            } else {
                const error = new Error(result.error?.message ?? 'execution failed');
                error.name = result.error?.name ?? 'Error';
                error.stack = result.error?.stack;
                void execution.appendOutput(new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error)]));
                execution.end(false, Date.now());
            }
        } catch (e) {
            const error = e instanceof Error ? e : new Error(String(e));
            void execution.appendOutput(new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error)]));
            execution.end(false, Date.now());
        } finally {
            this.activeExecutions.delete(cellId);
        }
    }

    dispose(): void {
        this.client?.dispose();
        this.controller.dispose();
        this.output.dispose();
    }
}

function toOutputItems(data: Record<string, unknown>): vscode.NotebookCellOutputItem[] {
    const items: vscode.NotebookCellOutputItem[] = [];
    for (const [mime, value] of Object.entries(data ?? {})) {
        const text = typeof value === 'string' ? value : JSON.stringify(value);
        items.push(vscode.NotebookCellOutputItem.text(text, mime));
    }
    if (items.length === 0) {
        items.push(vscode.NotebookCellOutputItem.text('', 'text/plain'));
    }
    return items;
}
