import type { ApiConnectionProvider, ApiDirective } from './api';

/**
 * Composing a connect-directive line from a connection-provider descriptor.
 *
 * This is a deliberate port of `editors/vscode/src/connectionDirective.ts`, kept
 * behaviourally identical so a connection built in the browser and one built in
 * VS Code produce the same line. It is duplicated rather than shared because the
 * two apps are separate TypeScript projects with no common package; if a third
 * client ever needs it, compose it in the kernel instead of copying it again.
 *
 * The descriptor says which flag carries each setting; the language's directive
 * table says how each flag binds (a value, or a bare switch).
 *
 * **One deliberate difference from the VS Code version.** There, a `secretRef`
 * setting is omitted from the line: the extension puts the secret itself into the
 * OS credential store over RPC and the notebook refers to it implicitly. A browser
 * cannot write to the credential store, so omitting it here would produce a
 * directive with no credential at all. Instead the field collects the *name* of an
 * already-stored secret and that name is written to the flag — which is exactly
 * what the invariant allows: a notebook carries a reference, never a password.
 * The wizard labels the field accordingly and shows the composed line before it is
 * inserted, so what lands in the file is never a surprise.
 */

/**
 * Kinds are compared without regard to case. The kernel's RPC camelCases them
 * ("secretRef"), while the Jobs API re-serializes the same descriptors with .NET's
 * declared names ("SecretRef") — because RunStatus and friends are public API and
 * must keep theirs. A `kind === 'secretRef'` that silently never matches would
 * write a credential into a notebook, so this compares loosely on purpose.
 */
export function sameKind(kind: string | undefined | null, expected: string): boolean {
  return (kind ?? '').toLowerCase() === expected.toLowerCase();
}

/** Wraps a value in quotes when it contains whitespace, so the kernel's tokenizer keeps it whole. */
export function quote(value: string): string {
  return /\s|"/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value;
}

function isFlagParameter(directive: ApiDirective | undefined, flag: string): boolean {
  const parameter = directive?.parameters?.find((p) => p.name === flag || p.aliases?.includes(flag));
  // DirectiveParameterKind on the wire: 'flag' is a bare switch; anything else takes a value.
  return sameKind(parameter?.kind, 'flag');
}

/**
 * Composes the connect line: `#!x-connect --name n --server v …`. Settings absent
 * from `values`, runtime-only settings, and secret settings are omitted — a
 * password is never written into a notebook, only a reference to one.
 *
 * A bool equal to its declared default is omitted, because an absent switch *is*
 * the default; other kinds are always stated, since the kernel's defaulting
 * ladders may infer differently (a user without `--auth` implies a SQL login).
 * Bools map to a bare switch when the directive declares one (`--trust-cert`),
 * otherwise to `--flag true|false`. An enum with no flag of its own emits
 * `--<value>` when the directive knows that switch (`--ssh` / `--winrm`).
 */
export function composeConnectDirective(
  provider: ApiConnectionProvider,
  directive: ApiDirective | undefined,
  values: Record<string, string | boolean | undefined>,
): string {
  const parts: string[] = [provider.connectSelector ?? ''];
  for (const setting of provider.settings) {
    const value = values[setting.name];
    // runtimeOnly settings are supplied when the cell runs and never belong in
    // the file. A secretRef does belong: it names a secret, it is not one.
    if (value === undefined || value === '' || setting.runtimeOnly) {
      continue;
    }
    const text = typeof value === 'boolean' ? String(value) : value.trim();
    if (text === '' || (sameKind(setting.kind, 'bool') && text === (setting.default ?? ''))) {
      continue;
    }
    if (!setting.directiveFlag) {
      const asSwitch = '--' + text;
      if (sameKind(setting.kind, 'enum') && isFlagParameter(directive, asSwitch)) {
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

/** True when this setting holds a credential the user must not type into a cell. */
export function isSecret(kind: string | undefined): boolean {
  return sameKind(kind, 'secretRef') || sameKind(kind, 'secret');
}
