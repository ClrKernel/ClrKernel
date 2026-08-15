using System.Collections.Generic;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// One registered connection, flattened for an editor UI. Provider-neutral: the fields a
/// particular language has no concept of are simply left null/false.
/// </summary>
public sealed class ConnectionInfo {
    /// <summary>The name the notebook refers to this connection by.</summary>
    public string Name { get; set; }

    /// <summary>A human-readable one-liner for the picker.</summary>
    public string Describe { get; set; }

    public string Server { get; set; }
    public string Database { get; set; }

    /// <summary>The auth mode's name, as the provider spells it.</summary>
    public string Auth { get; set; }

    /// <summary>The login, when the auth mode has one.</summary>
    public string User { get; set; }

    /// <summary>True when a password must be resolved before this connection can be used.</summary>
    public bool NeedsSecret { get; set; }

    /// <summary>The key the password is stored under — never the password.</summary>
    public string SecretRef { get; set; }

    public bool IsDefault { get; set; }
}

/// <summary>Whether a <c>connections.json</c> was found, and what it holds.</summary>
public sealed class ConnectionConfigStatus {
    public bool Found { get; set; }
    public string Path { get; set; }
    public IReadOnlyList<string> Names { get; set; } = new List<string>();
}

/// <summary>
/// A cell language's named connections, as an editor needs to see them: list, add, remove,
/// choose a default.
/// <para>
/// This exists so the JSON-RPC host can serve a connection UI for <em>any</em> language without
/// referencing that language's package — the same reason <see cref="ICellLanguageServices"/>
/// exists for completion and hover. Before it, the host had eight <c>clrkernel/sql/*</c> methods
/// and three <c>clrkernel/dax/*</c> methods that were the same four operations written twice.
/// </para>
/// </summary>
public interface IConnectionCatalog {
    /// <summary>The connection used when a cell names none, or null.</summary>
    string DefaultName { get; }

    /// <summary>Every registered connection, secret-free.</summary>
    IReadOnlyList<ConnectionInfo> List();

    /// <summary>
    /// Registers (or replaces) a connection from the language's own connect directive — the same
    /// <c>#!sql-connect</c> / <c>#!dax-connect</c> line a user could type. Keeping the directive as
    /// the wire format means the UI never has to model each provider's options.
    /// </summary>
    /// <param name="directive">The full connect-directive line, selector included.</param>
    /// <param name="secret">Stored against the connection's secret reference when supplied.
    /// Never persisted to the notebook or a config file.</param>
    /// <returns>The registered connection's name.</returns>
    string Add(string directive, string secret = null);

    /// <summary>Removes a connection. False when there was none by that name.</summary>
    bool Remove(string name);

    /// <summary>Makes <paramref name="name"/> the default.</summary>
    void SetDefault(string name);
}

/// <summary>
/// Implemented by an <see cref="IConnectionCatalog"/> whose connections can also live in a
/// <c>connections.json</c> beside the notebook.
/// <para>
/// Separate from <see cref="IConnectionCatalog"/> on purpose. Only SQL Server has config-file
/// support today, and folding these three members into the main interface would have made half of
/// it optional for every other provider — the usual way an abstraction starts lying. A language
/// gains config support by implementing this as well, and the host discovers that with a type
/// check rather than a capability flag.
/// </para>
/// </summary>
public interface IConfigBackedConnections {
    /// <summary>Looks for a config file at or above <paramref name="directory"/>.</summary>
    ConnectionConfigStatus Status(string directory);

    /// <summary>Registers this provider's entries from the nearest config file. Returns the names.</summary>
    IReadOnlyList<string> LoadFromConfig(string directory);

    /// <summary>Writes a registered connection into <paramref name="filePath"/>, secret-free.</summary>
    string SaveToConfig(string name, string filePath);
}
