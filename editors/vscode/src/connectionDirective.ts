import { DirectiveDefinition } from './languages';

/**
 * Composing a connect-directive line from a connection-provider descriptor —
 * the pure half of the generic connection wizard, kept free of any `vscode`
 * import so it is testable. The descriptor says which flag carries each
 * setting; the language's directive table says how each flag binds (value vs
 * bare switch). The secret VALUE never appears here: it rides the RPC's
 * `secret` parameter and lands in the OS credential store.
 */

export interface ConnectionSetting {
    name: string;
    aliases?: string[];
    displayName?: string;
    kind?: 'text' | 'bool' | 'int' | 'enum' | 'secretRef' | 'filePath' | 'keyValueBag';
    required?: boolean;
    oneOfGroup?: string | null;
    enumValues?: string[];
    default?: string | null;
    directiveFlag?: string | null;
    runtimeOnly?: boolean;
    description?: string;
}

export interface ConnectionProviderDescriptor {
    type: string;
    displayName: string;
    description?: string;
    languageIds?: string[];
    connectSelector?: string | null;
    settings: ConnectionSetting[];
    allowExtraSettings?: boolean;
}

/** Wraps a value in quotes when it contains whitespace, so the kernel's tokenizer keeps it whole. */
export function quote(value: string): string {
    return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

function isFlagParameter(directive: DirectiveDefinition | undefined, flag: string): boolean {
    const parameter = directive?.parameters?.find(
        (p) => p.name === flag || p.aliases?.includes(flag));
    // DirectiveParameterKind on the wire: 'flag' is a bare switch; anything else takes a value.
    return parameter?.kind === 'flag';
}

/**
 * Composes the connect line: `#!x-connect --name n --server v …`. Settings
 * absent from `values`, runtime-only settings, and secretRef settings (the
 * secret rides the RPC parameter) are omitted. A value equal to the setting's
 * declared default is omitted for bool settings — an absent switch is the
 * default by definition (the way the hand-written builders always did it);
 * other kinds are always stated because the kernel's defaulting ladders may
 * infer differently (e.g. a user without --auth implies a SQL login).
 * Bool settings map to bare switches when the directive
 * declares the flag as one (--trust-cert), else to `--flag true|false`. An
 * enum setting without a directive flag (PSRemoting's transport) emits
 * `--<value>` when the directive knows that switch (--ssh / --winrm).
 */
export function composeConnectDirective(
    provider: ConnectionProviderDescriptor,
    directive: DirectiveDefinition | undefined,
    values: Record<string, string | boolean | undefined>,
): string {
    const parts: string[] = [provider.connectSelector ?? ''];
    for (const setting of provider.settings) {
        const value = values[setting.name];
        if (value === undefined || value === '' || setting.runtimeOnly || setting.kind === 'secretRef') {
            continue;
        }
        const text = typeof value === 'boolean' ? String(value) : value.trim();
        if (text === '' || (setting.kind === 'bool' && text === (setting.default ?? ''))) {
            continue;
        }
        if (!setting.directiveFlag) {
            const asSwitch = '--' + text;
            if (setting.kind === 'enum' && isFlagParameter(directive, asSwitch)) {
                parts.push(asSwitch);
            }
            continue;
        }
        if (isFlagParameter(directive, setting.directiveFlag)) {
            if (text === 'true') {
                parts.push(setting.directiveFlag);
            }
            continue;
        }
        parts.push(setting.directiveFlag, quote(text));
    }
    return parts.filter((p) => p.length > 0).join(' ');
}
