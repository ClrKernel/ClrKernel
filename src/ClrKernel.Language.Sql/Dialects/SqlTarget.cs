using System;
using System.Collections.Generic;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;

/// <summary>
/// A named connection a cell can run on, and the provider that would carry it.
/// <para>
/// The session used to hold only SQL Server connections, so "which connection"
/// and "which provider" were the same question. With dialects they are two: a
/// cell says what language it is written in, a connection says what carries it,
/// and the pair is either compatible or it is not. This is the connection half.
/// </para>
/// </summary>
public sealed class SqlTarget {
    private SqlTarget(string name, string providerType, SqlConnectionSpec spec) {
        Name = name;
        ProviderType = providerType;
        SqlServerSpec = spec;
    }

    public string Name { get; }

    /// <summary>The <c>connections.json</c> <c>$type</c>: <c>SqlServer</c>,
    /// <c>Oracle</c>, <c>Odbc</c>, <c>Jdbc</c>, …</summary>
    public string ProviderType { get; }

    /// <summary>Set only for SQL Server, which keeps its own execution path —
    /// bulk copy, MERGE and the deploy planner all need the real client.</summary>
    public SqlConnectionSpec SqlServerSpec { get; }

    public bool IsSqlServer => SqlServerSpec != null;

    public static SqlTarget ForSqlServer(SqlConnectionSpec spec) =>
        new SqlTarget(spec.Name, "SqlServer", spec);

    public static SqlTarget ForProvider(string name, string providerType) =>
        new SqlTarget(name, providerType, null);

    /// <summary>
    /// Every connection this notebook can name, from every config file in scope —
    /// whatever its <c>$type</c>.
    /// <para>
    /// Deliberately not filtered to the types this session can open. A connection
    /// that exists but needs a package loaded should say so; one filtered out here
    /// would come back as "no connection named 'erp'", which sends the reader
    /// looking for a typo that is not there.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProviderTypesInConfig(string startDirectory = null) {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ConnectionConfig.FindFiles(startDirectory)) {
            foreach (var node in ConnectionConfig.LoadAllRaw(file)) {
                if (!string.IsNullOrWhiteSpace(node.Type)) {
                    // Later files overlay earlier ones, the same order LoadFromConfig
                    // uses: connections.local.json wins over the shared file.
                    types[node.Name] = node.Type;
                }
            }
        }
        return types;
    }
}
