using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// The secret values this process has seen, and the one operation on them:
/// replacing each with <c>***</c> in text that is about to leave.
///
/// <para>
/// The kernel is the one process that knows a secret's value — it resolved it —
/// so it is the one place a printed secret can be caught before it lands in a run
/// artifact, a log, a notification, or an <c>.ipynb</c> that gets committed.
/// Every byte of cell output leaves through a handful of chokepoints (the console
/// proxy, the MIME bundler, the execute error replies), and each of them calls
/// <see cref="Redact"/>. GitHub Actions does the same for <c>secrets.*</c>, and
/// for the same reason: people print things.
/// </para>
/// <para>
/// A safety net, not a boundary. A value that has been base64'd, split, or
/// JSON-escaped walks past this; and values shorter than <see cref="MinimumLength"/>
/// are not masked at all, because masking a six-character secret would mask every
/// six-character word that happens to match.
/// </para>
/// </summary>
public static class SecretRedaction {
    public const string Mask = "***";

    /// <summary>Below this a value is not registered: too short to mask without collateral.</summary>
    public const int MinimumLength = 8;

    /// <summary>The variable prefix Studio and the secret chain use; seeded from at start.</summary>
    public const string DefaultEnvironmentPrefix = "CLRKERNEL_SECRET_";

    private static readonly object _gate = new object();
    private static string[] _secrets = Array.Empty<string>();

    /// <summary>Remembers a value as secret. Null, blank and short values are ignored.</summary>
    public static void Register(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinimumLength) {
            return;
        }
        lock (_gate) {
            if (Array.IndexOf(_secrets, value) >= 0) {
                return;
            }
            // Longest first, so a secret that contains another is masked whole
            // rather than leaving its ends around a *** in the middle.
            _secrets = _secrets.Append(value).OrderByDescending(s => s.Length).ToArray();
        }
    }

    /// <summary>
    /// Registers every environment variable under <paramref name="prefix"/> —
    /// the way a branch's secrets reach a kernel Studio started, and the way a
    /// headless run is handed them. Called once as an engine starts, so a cell
    /// that reads the variable directly, or a shell cell that dumps the
    /// environment, is caught the same as one that went through the store.
    /// </summary>
    public static int SeedFromEnvironment(string prefix = DefaultEnvironmentPrefix) {
        var count = 0;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
            if (entry.Key is string key && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.Value is string value) {
                Register(value);
                count++;
            }
        }
        return count;
    }

    /// <summary>Whether anything is registered — so callers can skip the walk.</summary>
    public static bool Any => _secrets.Length > 0;

    /// <summary><paramref name="text"/> with every registered value replaced by <see cref="Mask"/>.</summary>
    public static string Redact(string text) {
        var secrets = _secrets;
        if (string.IsNullOrEmpty(text) || secrets.Length == 0) {
            return text;
        }
        foreach (var secret in secrets) {
            if (text.IndexOf(secret, StringComparison.Ordinal) >= 0) {
                text = text.Replace(secret, Mask);
            }
        }
        return text;
    }

    /// <summary>Redacts every string value in a MIME bundle, in place, and returns it.</summary>
    public static IDictionary<string, object> RedactAll(IDictionary<string, object> data) {
        if (data == null || !Any) {
            return data;
        }
        foreach (var key in data.Keys.ToList()) {
            if (data[key] is string text) {
                data[key] = Redact(text);
            }
        }
        return data;
    }

    /// <summary>Forgets everything. Tests only — a real process has no reason to.</summary>
    public static void Clear() {
        lock (_gate) {
            _secrets = Array.Empty<string>();
        }
    }
}
