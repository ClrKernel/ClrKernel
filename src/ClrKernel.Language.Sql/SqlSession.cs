using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Language.Sql;
/// <summary>
/// Holds the SQL connections for a notebook session and runs <c>#!sql</c>
/// cells. Query result sets are displayed as <see cref="DisplayTable"/> concepts
/// (drawn as the interactive grid by the registered formatters); the cell's own
/// return value is a short run summary. Passwords are resolved from the
/// <see cref="SecretStore"/> at execution time and never persisted.
/// </summary>
public sealed partial class SqlSession {
    private readonly SqlConnectionRegistry _registry = new SqlConnectionRegistry();
    private readonly SecretStore _secrets;

    /// <summary>Max rows materialized per grid (remaining rows are still counted).</summary>
    public int RowLimit { get; set; } = 1000;

    public SqlSession(SecretStore secrets = null) {
        _secrets = secrets ?? new SecretStore();
    }

    public SqlConnectionRegistry Connections => _registry;
    public SecretStore Secrets => _secrets;

    /// <summary>Registers a connection from a <c>#!sql-connect</c> line and returns the
    /// parsed directive (spec, default flag, and any C# variable to bind).</summary>
    public SqlConnectDirective Connect(string directiveLine) {
        var directive = SqlDirectives.ParseConnect(directiveLine);

        // Name-only (`#!sql-connect --name x [--default] [--var y]`): reference the
        // existing connection of that name — typically loaded from connections.json —
        // so the C# variable binds without restating (or clobbering) the definition.
        if (directive.IsReference) {
            if (!_registry.TryGet(directive.Spec.Name, out var existing)) {
                LoadFromConfig(); // a headless/Jupyter run may not have loaded the config yet
                existing = _registry.Resolve(directive.Spec.Name);
            }
            if (directive.IsDefault) {
                _registry.SetDefault(existing.Name);
            }
            return new SqlConnectDirective(existing, directive.IsDefault, directive.Variable, isReference: true);
        }

        _registry.Register(directive.Spec, directive.IsDefault);
        return directive;
    }

    /// <summary>Registers a pre-built spec (used by the connection UI).</summary>
    public void Register(SqlConnectionSpec spec, bool asDefault) => _registry.Register(spec, asDefault);

    /// <summary>Stores a password in the secret store; returns the provider used.</summary>
    public string StoreSecret(string secretRef, string secret) => _secrets.Store(secretRef, secret);

    /// <summary>Runs a T-SQL cell body and returns a run summary.</summary>
    public DisplayData Execute(string cellBody) => Execute(cellBody, null);

    /// <summary>
    /// Runs a cell body in one dialect, on whichever connection it names.
    /// <para>
    /// The dialect decides two things and neither of them is where the statement
    /// goes: which words are legal, and which providers may carry it. The
    /// connection decides the rest. A null dialect means T-SQL on SQL Server —
    /// what every caller meant before there was more than one.
    /// </para>
    /// </summary>
    public DisplayData Execute(string cellBody, SqlDialectLanguage dialect) {
        var request = SqlDirectives.ParseCell(cellBody);

        // A -- step cell defines a pipeline node: register it (its SQL runs later,
        // as part of #!sql-run), rather than executing now.
        if (!string.IsNullOrWhiteSpace(request.StepName)) {
            return RegisterStep(request);
        }

        var target = ResolveTarget(request.ConnectionName, dialect);
        if (!target.IsSqlServer) {
            return ExecuteOnProvider(target, request.Sql);
        }

        var spec = target.SqlServerSpec;

        // The syntax check belongs to the dialect, and only T-SQL has a parser.
        // Running it over an Oracle cell would reject valid Oracle, which is worse
        // than not checking: it is the editor being confidently wrong.
        if (dialect == null || dialect is SqlCellLanguage) {
            var diagnostics = TSqlSyntax.Check(request.Sql);
            if (diagnostics.Count > 0) {
                var first = diagnostics[0];
                throw new SqlCellException(
                    $"T-SQL syntax error (line {first.Line + 1}): {first.Message}");
            }
        }

        string connectionString;
        try {
            connectionString = spec.BuildConnectionString(_secrets);
        } catch (SecretNotFoundException e) {
            throw new SqlCellException(e.Message, e);
        }

        var stopwatch = Stopwatch.StartNew();
        var gridCount = 0;
        var totalRows = 0;
        int recordsAffected;
        try {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = request.Sql;
            using var reader = command.ExecuteReader();
            do {
                if (reader.FieldCount > 0) {
                    totalRows += RenderResultSet(reader);
                    gridCount++;
                }
            } while (reader.NextResult());
            recordsAffected = reader.RecordsAffected;
        } catch (SqlException e) {
            throw new SqlCellException(FormatSqlError(spec, e), e);
        }
        stopwatch.Stop();

        return Summary(spec, gridCount, totalRows, recordsAffected, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Which connection a cell runs on, and whether this dialect may run on it.
    /// <para>
    /// SQL Server connections come from the session registry, which is where
    /// <c>#!sql-connect</c> and the config loader put them. Anything else is a
    /// config node this session has not modelled — it is resolved by name and
    /// opened through its own provider package.
    /// </para>
    /// </summary>
    public SqlTarget ResolveTarget(string requestedName, SqlDialectLanguage dialect) {
        var target = Resolve(requestedName);
        if (dialect != null && !dialect.Supports(target.ProviderType)) {
            throw new SqlCellException(
                $"A {dialect.DisplayName} cell cannot run on '{target.Name}', which is a " +
                $"{target.ProviderType} connection. {dialect.DisplayName} runs on: " +
                $"{string.Join(", ", dialect.SupportedProviders)}. " +
                "Either point the cell at a different connection or change the cell's language.");
        }
        return target;
    }

    private SqlTarget Resolve(string requestedName) {
        if (!string.IsNullOrWhiteSpace(requestedName)
            && _registry.TryGet(requestedName, out var named)) {
            return SqlTarget.ForSqlServer(named);
        }

        var inConfig = SqlTarget.ProviderTypesInConfig();
        if (!string.IsNullOrWhiteSpace(requestedName)) {
            if (inConfig.TryGetValue(requestedName, out var type)) {
                return SqlTarget.ForProvider(requestedName, type);
            }
            // Not ours and not in any config file: the registry writes the message,
            // because it is the one that knows what names it does have.
            return SqlTarget.ForSqlServer(_registry.Resolve(requestedName));
        }

        // No name: the session default when there is one. A notebook whose only
        // connection is an Oracle node has no session default, and falling through
        // to the registry would say "no SQL connection is configured" while one is
        // sitting in connections.json — so a single config connection is taken as
        // the default, and several without a chosen one is said plainly.
        if (!_registry.IsEmpty) {
            return SqlTarget.ForSqlServer(_registry.Resolve(null));
        }
        if (inConfig.Count == 1) {
            var only = inConfig.First();
            return SqlTarget.ForProvider(only.Key, only.Value);
        }
        if (inConfig.Count > 1) {
            throw new SqlCellException(
                "This cell does not say which connection to run on, and there is no default. " +
                $"Name one: {string.Join(", ", inConfig.Keys)}.");
        }
        return SqlTarget.ForSqlServer(_registry.Resolve(null));
    }

    /// <summary>
    /// The path for everything that is not SQL Server: open the provider's own
    /// <see cref="DataSource"/> and read the results back through the same grid.
    /// <para>
    /// Deliberately separate from the SQL Server path rather than replacing it.
    /// That path carries bulk copy, MERGE, the deploy planner and error messages
    /// with SQL Server message numbers in them, and every notebook already written
    /// depends on it. A dialect feature is not a reason to re-route it.
    /// </para>
    /// </summary>
    private DisplayData ExecuteOnProvider(SqlTarget target, string sql) {
        var source = DataSourceCatalog.Open(target.ProviderType, target.Name, _secrets);
        var stopwatch = Stopwatch.StartNew();
        var gridCount = 0;
        var totalRows = 0;
        int recordsAffected;
        try {
            using var connection = source.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            do {
                if (reader.FieldCount > 0) {
                    totalRows += RenderResultSet(reader);
                    gridCount++;
                }
            } while (reader.NextResult());
            recordsAffected = reader.RecordsAffected;
        } catch (DbException e) {
            // No message number to quote: every provider numbers its own errors
            // differently, and inventing a shape for them would be a lie about
            // what the driver said.
            throw new SqlCellException($"{target.ProviderType} error on '{target.Name}': {e.Message}", e);
        }
        stopwatch.Stop();
        return Summary(target.Name, gridCount, totalRows, recordsAffected, stopwatch.ElapsedMilliseconds);
    }

    // Renders the current result set as an interactive grid (the same
    // sort/filter/analyze grid C# cells produce) and returns its total row
    // count. Reads to the end of the set so the caller can advance to the next.
    private int RenderResultSet(DbDataReader reader) {
        var fieldCount = reader.FieldCount;
        var columns = new string[fieldCount];
        var types = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++) {
            columns[i] = reader.GetName(i);
            Type fieldType;
            try {
                fieldType = reader.GetFieldType(i);
            } catch {
                fieldType = typeof(string);
            }
            types[i] = DisplayTable.KindOf(fieldType);
        }

        var rows = new List<IReadOnlyList<string>>();
        var total = 0;
        while (reader.Read()) {
            total++;
            if (rows.Count >= RowLimit) {
                continue; // keep counting for the "N of M" label
            }
            var row = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++) {
                row[i] = DisplayTable.CellText(reader.GetValue(i));
            }
            rows.Add(row);
        }

        // The concept, not a render: display it and the listening host draws the grid.
        new DisplayTable(null, columns, rows, types, total).Display();
        return total;
    }

    private DisplayData Summary(SqlConnectionSpec spec, int gridCount, int totalRows, int recordsAffected, long ms) =>
        Summary(spec.Name, gridCount, totalRows, recordsAffected, ms);

    private DisplayData Summary(string name, int gridCount, int totalRows, int recordsAffected, long ms) {
        var parts = new List<string>();
        if (gridCount == 0) {
            parts.Add(recordsAffected >= 0 ? $"{recordsAffected} row(s) affected" : "OK");
        } else {
            parts.Add(gridCount == 1 ? "1 result set" : $"{gridCount} result sets");
            if (recordsAffected > 0) {
                parts.Add($"{recordsAffected} row(s) affected");
            }
        }
        parts.Add($"{ms} ms");
        return MimeBundler.Bundle(new DisplayBadge(name, string.Join(" • ", parts)));
    }

    private static string FormatSqlError(SqlConnectionSpec spec, SqlException e) {
        var sb = new StringBuilder();
        sb.Append($"SQL error on '{spec.Name}'");
        if (e.Number != 0) {
            sb.Append($" (msg {e.Number}");
            if (e.Class != 0) {
                sb.Append($", level {e.Class}");
            }

            if (e.LineNumber != 0) {
                sb.Append($", line {e.LineNumber}");
            }

            sb.Append(')');
        }
        sb.Append(": ").Append(e.Message);
        return sb.ToString();
    }

}
