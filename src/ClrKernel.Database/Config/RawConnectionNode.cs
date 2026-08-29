using System;
using System.Collections.Generic;

namespace ClrKernel.Database;

/// <summary>
/// A connection config node read <b>without</b> resolving secrets — the opposite of
/// <see cref="ConnectionConfig.Load"/>, which resolves passwords from the secret store
/// eagerly. Used when we want the secret <i>reference</i> (e.g. to build a lazily-resolved
/// <c>SqlConnectionSpec</c>) rather than the password itself, so loading never fails just
/// because a password isn't present on this machine yet.
/// </summary>
public sealed class RawConnectionNode {
    internal RawConnectionNode(
        string name, string type, string sourceFile,
        IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> secretRefs) {
        Name = name;
        Type = type;
        SourceFile = sourceFile;
        Values = values;
        SecretRefs = secretRefs;
    }

    /// <summary>
    /// A node assembled in memory rather than read from a file — for a caller that
    /// already holds a connection's settings (the Jobs connection store) and wants the
    /// provider's own mapping applied to them. Going through this rather than building
    /// a provider spec by hand is what keeps the aliases, defaults and type inference
    /// in <c>SqlConnectionConfig.FromNode</c> as the only copy of that knowledge.
    /// </summary>
    public static RawConnectionNode FromValues(
        string name, string type,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> secretRefs = null) =>
        new RawConnectionNode(
            name, type, sourceFile: null,
            values ?? new Dictionary<string, string>(),
            secretRefs ?? new Dictionary<string, string>());

    /// <summary>
    /// A copy with one value replaced (or removed, for null). How a caller points the
    /// same saved connection at another database, or swaps in a second login, without
    /// knowing which connection-string keyword either becomes — that stays the
    /// provider's business.
    /// </summary>
    public RawConnectionNode With(string key, string value) {
        var values = new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase);
        if (value == null) {
            values.Remove(key);
        } else {
            values[key] = value;
        }
        // A secret reference for the same key would win over the value being set here,
        // which is never what the caller meant.
        var secretRefs = new Dictionary<string, string>(SecretRefs, StringComparer.OrdinalIgnoreCase);
        secretRefs.Remove(key);
        return new RawConnectionNode(Name, Type, SourceFile, values, secretRefs);
    }

    /// <summary>A copy with one secret reference replaced (or removed, for null).</summary>
    public RawConnectionNode WithSecret(string key, string secretRef) {
        var secretRefs = new Dictionary<string, string>(SecretRefs, StringComparer.OrdinalIgnoreCase);
        if (secretRef == null) {
            secretRefs.Remove(key);
        } else {
            secretRefs[key] = secretRef;
        }
        return new RawConnectionNode(Name, Type, SourceFile, Values, secretRefs);
    }

    /// <summary>The node name (the key in the config file).</summary>
    public string Name { get; }

    /// <summary>The <c>$type</c> discriminator (e.g. <c>SqlServer</c>), or null.</summary>
    public string Type { get; }

    /// <summary>The file the node came from.</summary>
    public string SourceFile { get; }

    /// <summary>Plain (non-secret) property values, keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Secret references (the raw <c>secret</c> strings), keyed by property name.</summary>
    public IReadOnlyDictionary<string, string> SecretRefs { get; }

    /// <summary>A plain property value, or <paramref name="fallback"/>.</summary>
    public string Get(string key, string fallback = null) =>
        Values.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>The secret reference for a property, or null if it isn't a secret.</summary>
    public string SecretRef(string key) =>
        SecretRefs.TryGetValue(key, out var reference) ? reference : null;

    /// <summary>True when the node's <c>$type</c> matches (case-insensitive).</summary>
    public bool IsType(string type) => string.Equals(Type, type, StringComparison.OrdinalIgnoreCase);
}
