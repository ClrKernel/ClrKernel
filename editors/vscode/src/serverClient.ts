import * as cp from 'child_process';
import * as rpc from 'vscode-jsonrpc/node';

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
 * Owns the ClrKernel.Server child process and the JSON-RPC connection over
 * its stdio (Content-Length framing on both sides).
 */
export class ServerClient {
    private process: cp.ChildProcess | undefined;
    private connection: rpc.MessageConnection | undefined;

    constructor(
        private readonly command: string,
        private readonly args: string[],
        private readonly cwd: string | undefined,
        private readonly log: (message: string) => void,
    ) { }

    async start(): Promise<void> {
        this.log(`starting server: ${this.command} ${this.args.join(' ')}`);
        const child = cp.spawn(this.command, this.args, { cwd: this.cwd });
        this.process = child;

        // Fail loudly (with a hint) if the command can't be spawned or dies
        // immediately — otherwise the first RPC write hits a destroyed stream
        // and surfaces as an unhelpful "write after a stream was destroyed".
        await new Promise<void>((resolve, reject) => {
            const fail = (reason: string) => reject(new Error(
                `Could not start ClrKernel server (${this.command} ${this.args.join(' ')}): ${reason}. ` +
                `Check the 'clrkernel.server.command' and 'clrkernel.server.args' settings — ` +
                `e.g. command "clrkernel" with args ["serve"], or "dotnet" with args ["<path>/ClrKernel.dll", "serve"].`));
            child.once('spawn', () => resolve());
            child.once('error', (err) => fail(err.message));
            child.once('exit', (exitCode) => fail(`exited immediately (${exitCode})`));
        });

        child.stderr?.on('data', (chunk: Buffer) => this.log(chunk.toString().trimEnd()));
        child.on('exit', (exitCode) => {
            this.log(`server exited (${exitCode})`);
            this.connection?.dispose();
            this.connection = undefined;
        });

        this.connection = rpc.createMessageConnection(
            new rpc.StreamMessageReader(child.stdout!),
            new rpc.StreamMessageWriter(child.stdin!),
        );
        this.connection.listen();

        const info = await this.connection.sendRequest('initialize');
        this.log(`connected: ${JSON.stringify(info)}`);
    }

    get running(): boolean {
        return this.connection !== undefined && this.process?.exitCode == null;
    }

    onDisplay(handler: (note: DisplayNotification) => void): void {
        this.connection?.onNotification('display', handler);
    }

    onUpdateDisplay(handler: (note: DisplayNotification) => void): void {
        this.connection?.onNotification('updateDisplay', handler);
    }

    execute(cellId: string, code: string): Promise<ExecuteResult> {
        if (!this.connection) {
            throw new Error('ClrKernel server is not running');
        }
        return this.connection.sendRequest<ExecuteResult>(
            'execute',
            rpc.ParameterStructures.byName as unknown as object,
            { cellId, code } as unknown as object,
        ) as Promise<ExecuteResult>;
    }

    dispose(): void {
        try {
            this.connection?.sendNotification('shutdown');
        } catch {
            // best effort
        }
        this.connection?.dispose();
        this.process?.kill();
        this.process = undefined;
        this.connection = undefined;
    }
}
