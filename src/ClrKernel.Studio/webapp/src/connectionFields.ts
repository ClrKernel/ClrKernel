import type { ApiConnectionProvider, ApiConnectionSetting } from './api';

/** The values a connection form holds, keyed by setting name. */
export type SettingValues = Record<string, string | boolean | undefined>;

export function filled(value: string | boolean | undefined): boolean {
  return typeof value === 'boolean' ? true : (value ?? '').trim().length > 0;
}

export function label(setting: ApiConnectionSetting): string {
  return setting.displayName ?? setting.name;
}

/** The one-of group names a provider declares, in the order its settings appear. */
export function groupsOf(provider: ApiConnectionProvider): string[] {
  const seen: string[] = [];
  for (const setting of provider.settings) {
    if (setting.oneOfGroup && !setting.runtimeOnly && !seen.includes(setting.oneOfGroup)) {
      seen.push(setting.oneOfGroup);
    }
  }
  return seen;
}

export function membersOf(provider: ApiConnectionProvider, group: string): ApiConnectionSetting[] {
  return provider.settings.filter((s) => s.oneOfGroup === group && !s.runtimeOnly);
}

/**
 * What is still missing before this connection could be opened, as things to say
 * out loud. Two rules, and the second is the one that is easy to miss:
 *
 * - a `required` setting with nothing in it;
 * - a one-of group with nothing chosen. Exactly one member of a group applies, so
 *   an empty group is incomplete — and members are usually *not* marked required
 *   individually (neither `server` nor `connectionString` is), which is precisely
 *   why the first rule cannot see it.
 */
export function unmet(provider: ApiConnectionProvider | null, values: SettingValues): string[] {
  if (provider == null) {
    return ['a connection type'];
  }
  const gaps = provider.settings
    // Group members are covered below, as a group; reporting them here as well
    // would ask for "Server" and "Server or Connection string" in one breath.
    .filter((s) => s.required && !s.runtimeOnly && !s.oneOfGroup && !filled(values[s.name]))
    .map(label);
  for (const group of groupsOf(provider)) {
    const members = membersOf(provider, group);
    if (!members.some((m) => filled(values[m.name]))) {
      gaps.push(members.map(label).join(' or '));
    }
  }
  const user = provider.settings.find((s) => s.name === 'user');
  if (user != null && signsInWithAName(provider, values) && !filled(values.user)) {
    gaps.push(label(user));
  }
  return gaps;
}

/**
 * Whether the chosen authentication is one that signs in with a name and a
 * password. A provider says so by listing those values in
 * <c>credentialValues</c>; the name they go with is the <c>user</c> setting, by
 * the same convention the connect-directive wizards follow.
 *
 * Worth checking here because the alternative is the driver's answer, and the
 * driver's answer to a missing user is <c>Login failed for user ''</c> — which
 * reads like a wrong password rather than an empty field.
 */
function signsInWithAName(provider: ApiConnectionProvider, values: SettingValues): boolean {
  return provider.settings.some(
    (s) => s.credentialValues != null
      && s.credentialValues.includes(String(values[s.name] ?? s.default ?? '')));
}
