import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    State,
    TransportKind,
} from 'vscode-languageclient/node';
import { LanguageDescriptor, currentLanguages, editorLanguageFor, setLanguages } from './languages';

export interface DisplayNotification {
    cellId: string;
    data: Record<string, unknown>;
    transient?: { display_id?: string };
}

export interface ExecuteResult {
    cellId: string;
    status: 'ok' | 'error';
    data?: Record<string, unknown> | null;
    error?: { name: string; message: string; stack?: string };
}

/**
 * Wraps the ClrKernel unified language server (`clrkernel lsp`). A single
 * LanguageClient owns one server process that provides BOTH standard LSP
 * language features (completion, hover, signature help — routed automatically
 * for csharp cells) AND cell execution via the custom `clrkernel/execute`
 * request and `clrkernel/display` notifications. One process means completion
 * sees the live REPL state.
 */
export class ServerClient {
    private client: LanguageClient | undefined;
    private reportedKernelVersion: string | undefined;
    private displayHandler?: (note: DisplayNotification) => void;
    private updateHandler?: (note: DisplayNotification) => void;

    constructor(
        private readonly command: string,
        private readonly args: string[],
        private readonly cwd: string | undefined,
        private readonly output: vscode.OutputChannel,
    ) { }

    async start(): Promise<void> {
        this.output.appendLine(`starting language server: ${this.command} ${this.args.join(' ')}`);

        const run = {
            command: this.command,
            args: this.args,
            transport: TransportKind.stdio,
            options: { cwd: this.cwd },
        };
        const serverOptions: ServerOptions = { run, debug: run };

        const clientOptions: LanguageClientOptions = {
            // Cell documents sync to the server, so completion/hover/signature help
            // work in cells. SQL cells also get live T-SQL diagnostics (the server
            // pushes textDocument/publishDiagnostics for sql documents).
            //
            // The selector is derived from the current language list (bundled until a
            // handshake has run) — a LanguageClient's selector is fixed at construction,
            // so a language plugged in mid-session gets editor features after the next
            // server (re)start, while execution works immediately.
            documentSelector: [
                { language: 'csharp-script' },
                ...currentLanguages().filter((l) => l.hasEditorServices)
                    .map((l) => ({ language: editorLanguageFor(l) })),
            ],
            outputChannel: this.output,
        };

        this.client = new LanguageClient('clrkernel', 'ClrKernel', serverOptions, clientOptions);
        await this.client.start();

        // The server reports its assembly version in the initialize response; the caller
        // compares it against what this extension was built for.
        const info = this.client.initializeResult?.serverInfo;
        this.reportedKernelVersion = info?.version;
        this.output.appendLine(`server reports ${info?.name ?? 'ClrKernel'} ${info?.version ?? '(no version)'}`);

        // The kernel's language list rides the handshake (0.10+). An older kernel
        // has no experimental payload: the bundled defaults stay in force.
        const experimental = this.client.initializeResult?.capabilities?.experimental as
            { clrkernel?: { languages?: LanguageDescriptor[] } } | undefined;
        setLanguages(experimental?.clrkernel?.languages);

        this.client.onNotification('clrkernel/display', (note: DisplayNotification) => this.displayHandler?.(note));
        this.client.onNotification('clrkernel/updateDisplay', (note: DisplayNotification) => this.updateHandler?.(note));
        this.client.onNotification('clrkernel/languagesChanged',
            (note: { languages?: LanguageDescriptor[] }) => setLanguages(note?.languages));
        this.output.appendLine('language server connected');
    }

    get running(): boolean {
        return this.client?.state === State.Running;
    }

    /** The kernel version from the initialize handshake, or undefined if it reported none. */
    get kernelVersion(): string | undefined {
        return this.reportedKernelVersion;
    }

    onDisplay(handler: (note: DisplayNotification) => void): void {
        this.displayHandler = handler;
    }

    onUpdateDisplay(handler: (note: DisplayNotification) => void): void {
        this.updateHandler = handler;
    }

    execute(cellId: string, code: string): Promise<ExecuteResult> {
        if (!this.client) {
            throw new Error('ClrKernel server is not running');
        }
        return this.client.sendRequest<ExecuteResult>('clrkernel/execute', { cellId, code });
    }

    /** Sends an arbitrary custom request (e.g. clrkernel/connections/*). */
    request<T>(method: string, params: unknown): Promise<T> {
        if (!this.client) {
            throw new Error('ClrKernel server is not running');
        }
        return this.client.sendRequest<T>(method, params);
    }

    async dispose(): Promise<void> {
        try {
            await this.client?.stop();
        } catch {
            // best effort
        }
        this.client = undefined;
    }
}
