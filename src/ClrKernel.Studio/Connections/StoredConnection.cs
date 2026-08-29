using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClrKernel.Studio;

/// <summary>Who a connection belongs to.</summary>
public enum ConnectionScope {
    /// <summary>Server-wide, managed by Server Admins, visible to everyone.</summary>
    Shared,

    /// <summary>
    /// One person's, invisible to everyone else — Server Admins included. That is
    /// not a convenience: it is why private connections are never committed to a
    /// branch, where any project viewer could read them.
    /// </summary>
    Private,
}

/// <summary>
/// One saved connection. The settings are held the way <c>connections.json</c> holds
/// them — a <c>$type</c> discriminator and a bag of provider-declared keys — so a
/// provider added later needs no field here, and materializing the store into a
/// config file is a copy rather than a translation.
/// <para>
/// No password is in this type, only the <em>name</em> of one. The value lives in the
/// OS credential store, which is the invariant every connection in this repo keeps:
/// a password written to config is a password that leaks with the config.
/// </para>
/// </summary>
public sealed class StoredConnection {
    /// <summary>Stable and never reused. Secret references are built from it, so a
    /// rename does not orphan a stored password.</summary>
    public string Id { get; set; }

    /// <summary>What notebooks reference (<c>#!sql-connect --name warehouse</c>), which
    /// is why it is unique rather than merely a label.</summary>
    public string Name { get; set; }

    public ConnectionScope Scope { get; set; }

    /// <summary>Set for <see cref="ConnectionScope.Private"/>, null for shared.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The <c>connections.json</c> <c>$type</c> — <c>SqlServer</c>, <c>Oracle</c>, …</summary>
    public string Type { get; set; }

    /// <summary>Provider-declared settings by their descriptor names (<c>server</c>,
    /// <c>database</c>, <c>auth</c>). Never a credential value.</summary>
    public Dictionary<string, string> Settings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The credential-store key holding the password, or null.</summary>
    public string SecretRef { get; set; }

    /// <summary>Do not persist a password at all — ask for one each session. The
    /// connection is unusable until someone supplies it, which is the point.</summary>
    public bool PromptForPassword { get; set; }

    /// <summary>
    /// The least-privilege login a non-admin executes as. This is the read-only
    /// boundary — the database's, not the app's. Without it a shared connection
    /// refuses execution to everyone below Server Admin, rather than running their
    /// statements as the writable login and hoping they were SELECTs.
    /// </summary>
    public string ReadOnlyUser { get; set; }

    public string ReadOnlySecretRef { get; set; }

    /// <summary>Seconds before a query is cancelled server-side.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Rows returned before the grid says "showing first N".</summary>
    public int RowCap { get; set; } = 10_000;

    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>True when <paramref name="userId"/> may see this connection at all.
    /// Being a Server Admin does not open somebody's private list.</summary>
    public bool VisibleTo(Guid userId) => Scope == ConnectionScope.Shared || OwnerId == userId;

    public StoredConnection Clone() {
        var copy = (StoredConnection)MemberwiseClone();
        copy.Settings = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>The credential-store key for this connection's password. Derived from
    /// the id so it survives a rename, and prefixed so it is recognisable in whatever
    /// keychain UI the operator opens.</summary>
    public string DefaultSecretRef => "clrkernel-studio:conn:" + Id;

    public string DefaultReadOnlySecretRef => DefaultSecretRef + ":ro";
}

/// <summary>
/// <c>connections.json</c> in the data directory — the one server-wide store, shared
/// and private entries together. Mirrors <see cref="ProjectsFile"/>: read whole,
/// written whole through a staging file so a crash mid-write cannot truncate it.
/// </summary>
public static class ConnectionsFile {
    public const string FileName = "connections.json";

    private static readonly JsonSerializerOptions _json = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    public static List<StoredConnection> Read(string dataDir) {
        var path = PathIn(dataDir);
        if (!File.Exists(path)) {
            return new List<StoredConnection>();
        }
        return JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(path), _json)
            ?? new List<StoredConnection>();
    }

    public static void Write(string dataDir, IEnumerable<StoredConnection> connections) {
        Directory.CreateDirectory(dataDir);
        var path = PathIn(dataDir);
        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(connections.ToList(), _json));
        File.Move(staging, path, overwrite: true);
    }
}
