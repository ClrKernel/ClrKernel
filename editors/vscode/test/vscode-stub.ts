/**
 * Stands in for the `vscode` module so extension code can be imported outside VS Code.
 *
 * Only the data types are real here — the ones a pure function actually touches. Anything that
 * would talk to the editor (windows, prompts, workspace edits) is deliberately absent: a test that
 * reaches for it should fail loudly rather than quietly exercise a fake UI, because a fake UI
 * proves nothing about the real one.
 */

export enum NotebookCellKind {
    Markup = 1,
    Code = 2,
}

export class NotebookCellData {
    metadata?: Record<string, unknown>;
    outputs?: unknown[];

    constructor(
        public kind: NotebookCellKind,
        public value: string,
        public languageId: string,
    ) { }
}

export class NotebookData {
    metadata?: Record<string, unknown>;

    constructor(public cells: NotebookCellData[]) { }
}

/** Marker so a test can assert it isn't accidentally depending on editor behaviour. */
export const window = new Proxy({}, {
    get(_target, name) {
        throw new Error(
            `vscode.window.${String(name)} was called from a unit test. This harness has no editor; ` +
            'extract the logic under test into a module that does not import vscode.',
        );
    },
});
