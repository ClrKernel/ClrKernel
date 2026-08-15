/**
 * Builds the `#!sql-connect` / `#!dax-connect` lines the connection buttons send to the kernel.
 *
 * Kept free of any `vscode` import on purpose: this is the contract with the kernel's directive
 * parsers, it is pure string work, and it is the part worth testing. The UI decides *what* to
 * connect to; this decides how to say it.
 */

/** Wraps a value in quotes when it contains whitespace, so the kernel's tokenizer keeps it whole. */
export function quote(value: string): string {
    return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

export interface SqlConnectOptions {
    name: string;
    server: string;
    database?: string;
    auth: string;
    user?: string;
    encrypt?: boolean;
    trustCert?: boolean;
}

export function buildSqlConnectDirective(opts: SqlConnectOptions): string {
    const parts = ['#!sql-connect', '--name', quote(opts.name), '--server', quote(opts.server)];
    if (opts.database) {
        parts.push('--database', quote(opts.database));
    }
    parts.push('--auth', opts.auth);
    if (opts.user) {
        parts.push('--user', quote(opts.user));
    }
    // Encryption defaults to on; only the non-default is worth stating.
    if (opts.encrypt === false) {
        parts.push('--encrypt', 'false');
    }
    if (opts.trustCert) {
        parts.push('--trust-cert');
    }
    return parts.join(' ');
}

/** Which kind of Analysis Services endpoint a cube points at. */
export type DaxCubeKind = 'fabric' | 'azure-as' | 'on-prem';

export interface DaxConnectOptions {
    name: string;
    kind: DaxCubeKind;
    /** Fabric only. */
    workspace?: string;
    /** Fabric only — the semantic model / dataset. */
    model?: string;
    /** Azure AS and on-prem. */
    server?: string;
    /** Azure AS and on-prem. */
    database?: string;
    /**
     * Use the signed-in Windows identity (`Integrated Security=SSPI`) instead of a token the
     * kernel fetches. Only meaningful for the two Entra-backed kinds, and only on Windows.
     */
    integrated?: boolean;
}

export function buildDaxConnectDirective(opts: DaxConnectOptions): string {
    const parts = ['#!dax-connect', '--name', quote(opts.name)];

    if (opts.kind === 'fabric') {
        parts.push('--fabric', '--workspace', quote(opts.workspace ?? ''), '--model', quote(opts.model ?? ''));
    } else {
        parts.push('--server', quote(opts.server ?? ''));
        if (opts.database) {
            parts.push('--database', quote(opts.database));
        }
        if (opts.kind === 'azure-as') {
            parts.push('--azure-as');
        }
    }

    // Entra is the default for both cloud kinds, so it carries no flag — an absent flag is a
    // choice, not a missing one. On-prem is Integrated already and takes no flag either.
    if (opts.integrated && opts.kind !== 'on-prem') {
        parts.push('--integrated');
    }
    return parts.join(' ');
}

/**
 * The next free `Untitled-N.nb.md` given the notebook paths already open.
 *
 * The double extension is the point: the notebook type is selected by the `*.nb.md` pattern, so a
 * new file called `Untitled.md` is not one of our notebooks and saving it needs the name corrected
 * by hand. Numbering matches the editor's own convention for untitled files.
 */
export function nextUntitledNotebookName(openPaths: readonly string[], suffix = '.nb.md'): string {
    const taken = new Set(openPaths.map((p) => p.replace(/^.*[\\/]/, '')));
    for (let n = 1; ; n++) {
        const candidate = `Untitled-${n}${suffix}`;
        if (!taken.has(candidate)) {
            return candidate;
        }
    }
}
