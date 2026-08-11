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
