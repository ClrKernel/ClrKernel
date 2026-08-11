namespace ClrKernel.Database;

/// <summary>
/// One property to write into a connection config node. A plain value is written as a
/// JSON string; a secret is written as <c>{ "secret": "&lt;ref&gt;" }</c> so the file
/// stays password-free (see <see cref="ConnectionConfig.Upsert"/>).
/// </summary>
public sealed class ConfigProperty {
    private ConfigProperty(string key, string value, bool isSecret) {
        Key = key;
        Value = value;
        IsSecret = isSecret;
    }

    /// <summary>The property name.</summary>
    public string Key { get; }

    /// <summary>The plain value, or the secret reference when <see cref="IsSecret"/>.</summary>
    public string Value { get; }

    /// <summary>True when <see cref="Value"/> is a secret reference, not a literal.</summary>
    public bool IsSecret { get; }

    /// <summary>A literal string property.</summary>
    public static ConfigProperty Plain(string key, string value) => new(key, value, isSecret: false);

    /// <summary>A <c>{ "secret": "&lt;ref&gt;" }</c> property.</summary>
    public static ConfigProperty Secret(string key, string secretRef) => new(key, secretRef, isSecret: true);
}
