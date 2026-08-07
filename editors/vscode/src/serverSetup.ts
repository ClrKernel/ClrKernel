import * as cp from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

const TOOL_PACKAGE = 'ClrKernel.Server';
const TOOL_COMMAND = 'clrkernel-server';
const DOTNET_DOWNLOAD_URL = 'https://dotnet.microsoft.com/download';

type Logger = (message: string) => void;

function run(command: string, args: string[], log: Logger): Promise<{ code: number; output: string }> {
    return new Promise((resolve) => {
        let output = '';
        const child = cp.spawn(command, args, { shell: false });
        const onData = (buffer: Buffer) => {
            const text = buffer.toString();
            output += text;
            log(text.trimEnd());
        };
        child.stdout?.on('data', onData);
        child.stderr?.on('data', onData);
        child.on('error', (error) => resolve({ code: -1, output: output + '\n' + error.message }));
        child.on('exit', (code) => resolve({ code: code ?? -1, output }));
    });
}

/** Absolute path to the global tool's launcher — used to dodge the PATH gap
 *  right after `dotnet tool install -g` (a fresh global tool isn't on the
 *  editor process's PATH until VS Code restarts). Returns the path only if it
 *  exists on disk. */
export function resolveGlobalToolPath(): string | undefined {
    const toolsDir = path.join(os.homedir(), '.dotnet', 'tools');
    const exe = process.platform === 'win32' ? `${TOOL_COMMAND}.exe` : TOOL_COMMAND;
    const full = path.join(toolsDir, exe);
    return fs.existsSync(full) ? full : undefined;
}

async function isDotnetAvailable(log: Logger): Promise<boolean> {
    const { code } = await run('dotnet', ['--version'], log);
    return code === 0;
}

/**
 * Called when the server couldn't be launched with the default command
 * (i.e. the user hasn't overridden it and the global tool isn't on PATH).
 * Diagnoses why and offers a one-click fix, mirroring how first-class
 * extensions prompt to install a missing runtime/tool.
 *
 * Returns an absolute command to launch on success, or undefined if the user
 * declined or the fix failed (the caller then surfaces the original error).
 */
export async function offerServerInstall(log: Logger): Promise<string | undefined> {
    if (!(await isDotnetAvailable(log))) {
        const pick = await vscode.window.showErrorMessage(
            'ClrKernel needs the .NET SDK (8.0+) to run notebooks, but `dotnet` was not found on your PATH.',
            'Get .NET',
        );
        if (pick === 'Get .NET') {
            void vscode.env.openExternal(vscode.Uri.parse(DOTNET_DOWNLOAD_URL));
        }
        return undefined;
    }

    const pick = await vscode.window.showInformationMessage(
        'ClrKernel.Server is not installed. Install it as a global .NET tool now?',
        { modal: true, detail: `Runs: dotnet tool install --global ${TOOL_PACKAGE}` },
        'Install',
    );
    if (pick !== 'Install') {
        return undefined;
    }

    const ok = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Installing ClrKernel.Server…', cancellable: false },
        async () => {
            let result = await run('dotnet', ['tool', 'install', '--global', TOOL_PACKAGE], log);
            if (result.code !== 0 && /already installed/i.test(result.output)) {
                result = await run('dotnet', ['tool', 'update', '--global', TOOL_PACKAGE], log);
            }
            return result.code === 0;
        },
    );

    if (!ok) {
        const pickFail = await vscode.window.showErrorMessage(
            'Installing ClrKernel.Server failed. See the ClrKernel output for details.',
            'Show Output',
        );
        if (pickFail === 'Show Output') {
            // caller owns the channel; the message already routed there via log
        }
        return undefined;
    }

    // Prefer the absolute path so this session works without a VS Code restart.
    const resolved = resolveGlobalToolPath();
    void vscode.window.showInformationMessage('ClrKernel.Server installed.');
    return resolved ?? TOOL_COMMAND;
}
