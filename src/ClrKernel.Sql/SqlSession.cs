using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ClrKernel.Data.Secrets;
using ClrKernel.Primitives;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Sql;
/// <summary>
/// Holds the SQL connections for a notebook session and runs <c>#!sql</c>
/// cells. Query result sets render as interactive grids (via
/// <see cref="DisplayExtensions.DisplayTable(System.Data.IDataReader, int)"/>);
/// the cell's own return value is a short run summary. Passwords are resolved
/// from the <see cref="SecretStore"/> at execution time and never persisted.
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
        _registry.Register(directive.Spec, directive.IsDefault);
        return directive;
    }

    /// <summary>Registers a pre-built spec (used by the connection UI).</summary>
    public void Register(SqlConnectionSpec spec, bool asDefault) => _registry.Register(spec, asDefault);

    /// <summary>Stores a password in the secret store; returns the provider used.</summary>
    public string StoreSecret(string secretRef, string secret) => _secrets.Store(secretRef, secret);

    /// <summary>Runs a <c>#!sql</c> cell body and returns a run summary.</summary>
    public DisplayData Execute(string cellBody) {
        var request = SqlDirectives.ParseCell(cellBody);

        // A -- step cell defines a pipeline node: register it (its SQL runs later,
        // as part of #!sql-run), rather than executing now.
        if (!string.IsNullOrWhiteSpace(request.StepName)) {
            return RegisterStep(request);
        }

        var spec = _registry.Resolve(request.ConnectionName);

        var diagnostics = TSqlSyntax.Check(request.Sql);
        if (diagnostics.Count > 0) {
            var first = diagnostics[0];
            throw new SqlCellException(
                $"T-SQL syntax error (line {first.Line + 1}): {first.Message}");
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

    // Renders the current result set as an interactive grid (the same
    // sort/filter/analyze grid C# cells produce) and returns its total row
    // count. Reads to the end of the set so the caller can advance to the next.
    private int RenderResultSet(SqlDataReader reader) {
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
            types[i] = InteractiveTable.KindOf(fieldType);
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
                row[i] = InteractiveTable.CellText(reader.GetValue(i));
            }
            rows.Add(row);
        }

        var html = InteractiveTable.Render(columns, rows, types, total);
        var text = $"[{total} row(s), {fieldCount} column(s)]";
        DisplayDataEmitter.Emit(new DisplayData(text, html));
        return total;
    }

    private DisplayData Summary(SqlConnectionSpec spec, int gridCount, int totalRows, int recordsAffected, long ms) {
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
        var text = $"{spec.Name}: {string.Join(" • ", parts)}";

        var html =
            "<div style=\"font:12px/1.5 -apple-system,Segoe UI,sans-serif;color:#57606a;" +
            "padding:2px 0\">" +
            $"<span style=\"display:inline-block;padding:1px 6px;border-radius:10px;" +
            $"background:#ddf4ff;color:#0969da;margin-right:6px\">{Encode(spec.Name)}</span>" +
            Encode(string.Join(" • ", parts)) + "</div>";

        return new DisplayData(text, html);
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

    private static string Encode(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
