/**
 * The extension's view of the kernel's cell languages. Everything language-
 * specific — language tags, selectors, which languages get LSP features or a
 * connections UI — flows from the LanguageDescriptor list the kernel serves in
 * its initialize handshake (and updates via clrkernel/languagesChanged when a
 * plugin registers mid-session).
 *
 * The bundled list below mirrors the languages shipped in the paired kernel.
 * It is the fallback for two moments the live list can't cover: before the
 * server has started (the markdown serializer runs at file open), and against
 * an older kernel that doesn't serve descriptors. Once descriptors arrive they
 * replace it wholesale.
 */

export interface LanguageDescriptor {
    id: string;
    displayName: string;
    defaultSelector: string | null;
    selectors: string[];
    languageTags: string[];
    hasEditorServices?: boolean;
    hasConnections?: boolean;
    configBacked?: boolean;
    directives?: DirectiveDefinition[];
    /** What a picker groups this under — 'SQL' clusters the dialects. VS Code's
     *  cell language picker is VS Code's own and cannot be grouped, so this is
     *  carried for completeness and used by the web app. */
    category?: string | null;
    /** The connections.json `$type`s this language's cells can run on. A
     *  compatibility declaration, not part of the language's identity. */
    supportedProviders?: string[];
    /** The id an editor should give this language's cells, when it differs from
     *  `id`. Distinct per language: it is what a cell is *called* here, and
     *  therefore what the kernel resolves the document back from. */
    editorLanguageId?: string;
    /** The syntax to highlight those cells with when the editor has no grammar
     *  of its own for `editorLanguageId`. Several languages may share one. Used
     *  by the web app's Monaco; here it is the package.json grammar that
     *  delegates, so this is carried and not read. */
    grammarId?: string;
}

export interface DirectiveDefinition {
    selector: string;
    description?: string;
    parameters?: DirectiveParameter[];
}

export interface DirectiveParameter {
    name: string;
    aliases?: string[];
    kind?: 'value' | 'flag' | 'keyValue' | 'forbidden';
    required?: boolean;
    enumValues?: string[];
    description?: string;
}

/** The languages shipped in the paired kernel line — the pre-handshake fallback. */
export const bundledLanguages: LanguageDescriptor[] = [
    { id: 'http', displayName: 'HTTP', defaultSelector: '#!http', selectors: ['#!http'], languageTags: ['http'] },
    { id: 'mermaid', displayName: 'Mermaid', defaultSelector: '#!mermaid', selectors: ['#!mermaid'], languageTags: ['mermaid'] },
    {
        id: 'powershell', displayName: 'PowerShell', defaultSelector: '#!pwsh',
        selectors: ['#!pwsh', '#!powershell', '#!pwsh-connect'],
        languageTags: ['pwsh', 'powershell', 'ps1'], hasEditorServices: true,
    },
    {
        id: 'shellscript', displayName: 'Shell', defaultSelector: '#!bash',
        selectors: ['#!bash', '#!zsh', '#!sh', '#!shell', '#!shell-connect'],
        languageTags: ['bash', 'zsh', 'sh', 'shell'],
    },
    {
        id: 'sql', displayName: 'T-SQL', defaultSelector: '#!sql',
        selectors: ['#!sql', '#!sql-connect', '#!sql-bulk', '#!sql-merge', '#!sql-run', '#!sql-deploy'],
        languageTags: ['sql', 'tsql'], hasEditorServices: true, hasConnections: true, configBacked: true,
        category: 'SQL', editorLanguageId: 'clr-sql', grammarId: 'sql',
        supportedProviders: ['SqlServer', 'Odbc', 'Jdbc'],
    },
    {
        id: 'oraclesql', displayName: 'Oracle SQL', defaultSelector: '#!oraclesql',
        selectors: ['#!oraclesql'], languageTags: ['oraclesql', 'plsql'], hasEditorServices: true,
        category: 'SQL', editorLanguageId: 'clr-oraclesql', grammarId: 'sql',
        supportedProviders: ['Oracle', 'Odbc', 'Jdbc'],
    },
    {
        id: 'ansisql', displayName: 'SQL (Generic)', defaultSelector: '#!ansisql',
        selectors: ['#!ansisql'], languageTags: ['ansisql'], hasEditorServices: true,
        category: 'SQL', editorLanguageId: 'clr-ansisql', grammarId: 'sql',
        supportedProviders: ['Odbc', 'Jdbc'],
    },
    {
        id: 'dax', displayName: 'DAX', defaultSelector: '#!dax',
        selectors: ['#!dax', '#!dax-connect'],
        languageTags: ['dax'], hasEditorServices: true, hasConnections: true, configBacked: true,
    },
];

let current: LanguageDescriptor[] = bundledLanguages;
const listeners: Array<(languages: LanguageDescriptor[]) => void> = [];

/** The live language list: kernel-served once connected, bundled before that. */
export function currentLanguages(): LanguageDescriptor[] {
    return current;
}

/** Replaces the live list (initialize handshake / languagesChanged notification). */
export function setLanguages(languages: LanguageDescriptor[] | undefined | null): void {
    if (!languages || languages.length === 0) {
        return; // an old kernel serves none: keep the bundled fallback
    }
    current = languages;
    for (const listener of listeners) {
        listener(current);
    }
}

export function onLanguagesChanged(listener: (languages: LanguageDescriptor[]) => void): void {
    listeners.push(listener);
}

/**
 * The id VS Code should give a cell in this language.
 *
 * Usually the kernel's own id, and for the SQL dialects deliberately not. A
 * language id is global in VS Code and the built-ins are registered first, so a
 * cell called `sql` wears the built-in's name in every menu — "SQL", never
 * "T-SQL", whatever the kernel calls itself — and every SQL extension the user
 * has installed attaches to it and adds its own completions. `csharp-script`
 * exists for exactly those two reasons; this is the same move, and the kernel
 * says which languages want it rather than this file keeping a list.
 */
export function editorLanguageFor(language: LanguageDescriptor): string {
    return language.editorLanguageId ?? language.id;
}

/**
 * Back the other way: the cell language behind a VS Code language id.
 *
 * Everything that starts from `cell.document.languageId` comes through here.
 * Matching the kernel's own id as well is not a courtesy — a notebook opened
 * before the ids changed, or one an older kernel described, still has cells
 * called `sql`, and they should keep working.
 */
export function languageForEditorLanguage(languageId: string): LanguageDescriptor | undefined {
    return current.find((l) => editorLanguageFor(l) === languageId)
        ?? current.find((l) => l.id === languageId);
}

const csharpTags = new Set(['csharp', 'c#', 'cs']);

/** The descriptor claiming a language tags, or undefined (C# and unknown tags). */
export function languageForTag(tag: string): LanguageDescriptor | undefined {
    const t = tag.toLowerCase();
    if (csharpTags.has(t)) {
        return undefined;
    }
    return current.find((l) => l.languageTags.some((x) => x.toLowerCase() === t));
}

/** True when the tag is one of the C# spellings. */
export function isCSharpTag(tag: string): boolean {
    return csharpTags.has(tag.toLowerCase());
}

/** True when the text already leads with one of the language's selectors as a whole token. */
export function startsWithSelector(language: LanguageDescriptor, text: string): boolean {
    const lead = text.replace(/^\s+/, '').toLowerCase();
    return language.selectors.some((s) => {
        const selector = s.toLowerCase();
        return lead.startsWith(selector) && (lead.length === selector.length || /\s/.test(lead[selector.length]));
    });
}

/** The selector to prepend for a language tags: the tag's own selector when registered, else the default. */
export function selectorForTag(language: LanguageDescriptor, tag: string): string | null {
    const own = language.selectors.find((s) => s.toLowerCase() === '#!' + tag.toLowerCase());
    return own ?? language.defaultSelector;
}

/**
 * The language tags to write when serializing a cell: the tag named by a leading
 * selector when it is one of the language's tags, else the tag matching the
 * language id, else the first tag. Keeps ```powershell and ```sql stable and
 * lets a #!zsh cell round-trip to its own tag.
 */
export function tagForCell(language: LanguageDescriptor, cellValue: string): string {
    const selector = /^\s*#!([^\s]+)/.exec(cellValue);
    if (selector) {
        const named = selector[1].toLowerCase();
        if (language.languageTags.some((t) => t.toLowerCase() === named)) {
            return named;
        }
    }
    const idTag = language.languageTags.find((t) => t.toLowerCase() === language.id.toLowerCase());
    return idTag ?? language.languageTags[0];
}
