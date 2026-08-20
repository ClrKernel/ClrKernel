using System;
using System.Collections.Generic;

namespace ClrKernel.Core.Primitives;

/// <summary>How a connection setting is edited and validated in generated UI.</summary>
public enum ConnectionSettingKind {
    Text,
    Bool,
    Int,
    Enum,
    /// <summary>A secret <b>reference</b> (credential-store key / CLRKERNEL_SECRET_*).
    /// The secret value itself never appears in config, notebooks, or this model.</summary>
    SecretRef,
    FilePath,
    /// <summary>An open key=value bag (ODBC keywords, JDBC properties).</summary>
    KeyValueBag,
}

/// <summary>
/// One configurable setting of a connection type: its connections.json key (with
/// the read-side aliases that key accepts), how it is edited, and which connect
/// directive flag carries it. The single source of truth generated config UIs
/// and the drift-guard tests read.
/// </summary>
public sealed class ConnectionSetting {
    /// <summary>Canonical connections.json key, e.g. <c>server</c>.</summary>
    public string Name { get; init; }

    /// <summary>Alternate keys accepted on read (<c>host</c>, <c>username</c>) — declared
    /// here so the alias knowledge lives in one place.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    public string DisplayName { get; init; }

    public ConnectionSettingKind Kind { get; init; } = ConnectionSettingKind.Text;

    public bool Required { get; init; }

    /// <summary>Settings sharing a group name are alternatives — exactly one of the
    /// group applies (ODBC's driver | dsn | connectionString).</summary>
    public string OneOfGroup { get; init; }

    public IReadOnlyList<string> EnumValues { get; init; }

    /// <summary>Default value as a string (Oracle port <c>1521</c>).</summary>
    public string Default { get; init; }

    /// <summary>The connect-directive flag a wizard emits this setting as
    /// (<c>--server</c>), or null when the setting has no directive form.</summary>
    public string DirectiveFlag { get; init; }

    /// <summary>Never serialized or shown: the setting is rebuilt at run time
    /// (a token-provider delegate, a runtime-discovered endpoint).</summary>
    public bool RuntimeOnly { get; init; }

    public string Description { get; init; }
}

/// <summary>
/// A connection type's self-description: its connections.json <c>$type</c>, the
/// languages whose connection UI should offer it, the connect directive it is
/// created with, and its settings schema. Providers declare one of these; front
/// ends render connection UIs from it instead of hard-coding each provider.
/// </summary>
public sealed class ConnectionProviderDescriptor {
    /// <summary>The connections.json <c>"$type"</c> discriminator, e.g. <c>SqlServer</c>.
    /// Not unique across providers by itself — <c>Ssh</c> nodes are shared by the
    /// shell and PowerShell languages — so lookups go by language, never by type.</summary>
    public string Type { get; init; }

    public string DisplayName { get; init; }

    public string Description { get; init; }

    /// <summary>Cell-language ids whose connection UI offers this provider
    /// (<c>Ssh</c> → shellscript and powershell). Empty for providers used only
    /// from C# cells (Fabric, Oracle, Odbc, Jdbc today).</summary>
    public IReadOnlyList<string> LanguageIds { get; init; } = Array.Empty<string>();

    /// <summary>The connect directive a wizard composes (<c>#!sql-connect</c>), or
    /// null when the provider has no directive form.</summary>
    public string ConnectSelector { get; init; }

    public IReadOnlyList<ConnectionSetting> Settings { get; init; } = Array.Empty<ConnectionSetting>();

    /// <summary>Keys beyond <see cref="Settings"/> are legal and passed through
    /// verbatim (ODBC connection-string keywords, JDBC driver properties).</summary>
    public bool AllowExtraSettings { get; init; }
}
