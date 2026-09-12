using System;
using System.Data;
using Azure.Core;
using ClrKernel.Database.Entra;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace ClrKernel.Database.Provider.Kusto;

/// <summary>
/// Entry point for Kusto — Azure Data Explorer, a Fabric Eventhouse / KQL
/// database, Log Analytics through its ADX proxy. From a C# cell:
/// <code>
/// var db = KustoDb.Connect("https://help.kusto.windows.net", "Samples");
/// db.Query("StormEvents | take 10")
/// </code>
/// All sign-in is Microsoft Entra; no passwords are handled here.
/// <para>Not <c>Kusto</c>: that is the root namespace of the client library, and a
/// C# cell that references it would resolve the bare name to the namespace.</para>
/// </summary>
public static class KustoDb {
    /// <summary>Default Entra credential chain, then a browser sign-in — a developer at a notebook.</summary>
    public static KustoDatabase Connect(string cluster, string database) =>
        WithCredential(cluster, database, EntraAuth.DefaultThenInteractiveBrowser());

    /// <summary>A browser sign-in every time, so you pick the account.</summary>
    public static KustoDatabase Interactive(string cluster, string database) =>
        WithCredential(cluster, database, EntraAuth.InteractiveOnly());

    /// <summary>An Entra service principal (client secret).</summary>
    public static KustoDatabase ClientSecret(string cluster, string database, string tenantId, string clientId, string clientSecret) =>
        WithCredential(cluster, database, EntraAuth.ClientSecret(tenantId, clientId, clientSecret));

    /// <summary>A caller-supplied Azure <see cref="TokenCredential"/>.</summary>
    public static KustoDatabase WithCredential(string cluster, string database, TokenCredential credential) {
        ArgumentNullException.ThrowIfNull(credential);
        return new KustoDatabase(new KustoConnectionSpec { Cluster = cluster, Database = database, Auth = KustoAuthMode.Entra, Credential = credential });
    }

    /// <summary>A database from a spec — what <c>#!kql-connect</c> registers.</summary>
    public static KustoDatabase FromSpec(KustoConnectionSpec spec) => new KustoDatabase(spec);
}

public enum KustoAuthMode {
    /// <summary>The default Entra chain (az CLI, VS, environment…), then a browser.</summary>
    Entra,
    /// <summary>A browser sign-in every time.</summary>
    Interactive,
    /// <summary>A service principal: tenant, client id, and a client secret from the secret store.</summary>
    ClientSecret,
}

/// <summary>How to reach one Kusto database. Secret-free: a client secret is a reference.</summary>
public sealed class KustoConnectionSpec {
    /// <summary>The cluster URL, e.g. <c>https://help.kusto.windows.net</c> or a Fabric Eventhouse query URI.</summary>
    public string Cluster { get; set; }
    public string Database { get; set; }
    public KustoAuthMode Auth { get; set; } = KustoAuthMode.Entra;
    public string TenantId { get; set; }
    public string ClientId { get; set; }
    /// <summary>The secret store reference for the client secret — never the secret itself.</summary>
    public string SecretRef { get; set; }
    /// <summary>The resolved client secret, set by whoever resolved <see cref="SecretRef"/>. Never serialized.</summary>
    public string ClientSecret { get; set; }
    /// <summary>A credential supplied in code; wins over <see cref="Auth"/> when set.</summary>
    public TokenCredential Credential { get; set; }

    public string Describe() => $"{Cluster} / {Database} ({Auth})";

    internal TokenCredential ResolveCredential() {
        if (Credential != null) {
            return Credential;
        }
        return Auth switch {
            KustoAuthMode.Interactive => EntraAuth.InteractiveOnly(),
            KustoAuthMode.ClientSecret => string.IsNullOrWhiteSpace(ClientSecret)
                ? throw new InvalidOperationException(
                    $"Kusto connection to {Cluster} uses a client secret, but none was resolved" +
                    (string.IsNullOrWhiteSpace(SecretRef) ? "." : $" for '{SecretRef}'."))
                : EntraAuth.ClientSecret(TenantId, ClientId, ClientSecret),
            _ => EntraAuth.DefaultThenInteractiveBrowser(),
        };
    }
}

/// <summary>One Kusto database: run KQL, or a management command.</summary>
public sealed class KustoDatabase : IDisposable {
    private readonly Lazy<ICslQueryProvider> _query;
    private readonly Lazy<ICslAdminProvider> _admin;

    public KustoConnectionSpec Spec { get; }
    public string Cluster => Spec.Cluster;
    public string Database => Spec.Database;

    public KustoDatabase(KustoConnectionSpec spec) {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Cluster)) {
            throw new ArgumentException("A Kusto connection needs a cluster URL.", nameof(spec));
        }
        if (string.IsNullOrWhiteSpace(spec.Database)) {
            throw new ArgumentException("A Kusto connection needs a database.", nameof(spec));
        }
        _query = new Lazy<ICslQueryProvider>(() => KustoClientFactory.CreateCslQueryProvider(Builder()));
        _admin = new Lazy<ICslAdminProvider>(() => KustoClientFactory.CreateCslAdminProvider(Builder()));
    }

    private KustoConnectionStringBuilder Builder() =>
        new KustoConnectionStringBuilder(Spec.Cluster, Spec.Database)
            .WithAadAzureTokenCredentialsAuthentication(Spec.ResolveCredential());

    /// <summary>Runs KQL and returns the primary result as a table (the interactive grid in a cell).</summary>
    public DataTable Query(string kql) {
        if (string.IsNullOrWhiteSpace(kql)) {
            throw new ArgumentException("kql is required.", nameof(kql));
        }
        using var reader = _query.Value.ExecuteQuery(Spec.Database, kql, new ClientRequestProperties());
        return Load(reader);
    }

    /// <summary>Runs a management command (<c>.show tables</c>, <c>.create table …</c>) and returns its result.</summary>
    public DataTable Execute(string command) {
        if (string.IsNullOrWhiteSpace(command)) {
            throw new ArgumentException("command is required.", nameof(command));
        }
        using var reader = _admin.Value.ExecuteControlCommand(Spec.Database, command, new ClientRequestProperties());
        return Load(reader);
    }

    // The first result set is the primary result; the rest (query properties,
    // completion info) are the client's business.
    private static DataTable Load(IDataReader reader) {
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    public void Dispose() {
        if (_query.IsValueCreated) {
            _query.Value.Dispose();
        }
        if (_admin.IsValueCreated) {
            _admin.Value.Dispose();
        }
    }
}
