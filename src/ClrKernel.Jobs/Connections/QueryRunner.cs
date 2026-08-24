using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

/// <summary>One grid of results. Cell values are already text, by
/// <see cref="DisplayTable"/>'s convention, so the browser renders what the notebook
/// grid would.</summary>
public sealed class QueryResultSet {
    public IReadOnlyList<string> Columns { get; set; }

    /// <summary>Per column: <c>number</c>, <c>date</c> or <c>string</c>, for
    /// type-aware sorting in the grid.</summary>
    public IReadOnlyList<string> Types { get; set; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; set; }

    /// <summary>The cap stopped this set short. There is no total: knowing it would
    /// mean a second query, and the honest thing to say is "first N".</summary>
    public bool Truncated { get; set; }
}

/// <summary>What one execution produced.</summary>
public sealed class QueryResult {
    public IReadOnlyList<QueryResultSet> ResultSets { get; set; } = Array.Empty<QueryResultSet>();

    /// <summary>Row counts, <c>PRINT</c> output and server warnings, in order — the
    /// Messages tab.</summary>
    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();

    public int RowsAffected { get; set; }
    public double ElapsedMs { get; set; }
    public bool Canceled { get; set; }
    public string Error { get; set; }
}

/// <summary>
/// Runs a statement against a saved connection, in this process.
/// <para>
/// In-process rather than through a kernel because of what the spec asks for:
/// cancelling a running query, connection pooling and a row cap are all things
/// ADO.NET does here and nothing can do over the kernel's RPC — neither surface can
/// interrupt a running cell, so "Cancel" there would mean killing the process.
/// </para>
/// </summary>
public sealed class QueryRunner {
    /// <summary>A query in flight. <see cref="Cancelled"/> is set by the cancel route
    /// and is what the outcome is decided from — see <see cref="RunAsync"/>.</summary>
    private sealed class Active {
        public Active(Guid actor, SqlCommand command) {
            Actor = actor;
            Command = command;
        }

        public Guid Actor { get; }
        public SqlCommand Command { get; }
        public volatile bool Cancelled;
    }

    private readonly SecretStore _secrets;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Active> _running = new(StringComparer.Ordinal);

    public QueryRunner(SecretStore secrets, ILogger<QueryRunner> logger) {
        _secrets = secrets ?? new SecretStore();
        _logger = logger;
    }

    /// <summary>
    /// Builds the live spec for a connection.
    /// <paramref name="leastPrivilege"/> swaps in the read-only login — the actual
    /// read-only boundary, since no amount of statement inspection makes a writable
    /// login safe.
    /// </summary>
    public SqlConnectionSpec SpecFor(StoredConnection connection, bool leastPrivilege) {
        // Through the provider's own mapping rather than field by field, so aliases,
        // defaults and the raw-connection-string inference stay in one place.
        var spec = SqlConnectionConfig.FromNode(RawConnectionNode.FromValues(
            connection.Name, connection.Type, connection.Settings,
            connection.SecretRef == null
                ? null
                : new Dictionary<string, string> { ["password"] = connection.SecretRef }));
        if (leastPrivilege) {
            spec.User = connection.ReadOnlyUser;
            spec.SecretRef = connection.ReadOnlySecretRef;
            // A least-privilege login is a login: whatever the connection's own auth
            // mode was, this one signs in with a name and a password.
            if (!spec.NeedsSecret) {
                spec.Auth = SqlAuthMode.SqlPassword;
            }
        }
        return spec;
    }

    /// <summary>Opens and closes a connection, to prove the settings and credential
    /// work before anyone types a query against them.</summary>
    public async Task<string> TestAsync(
        StoredConnection connection, bool leastPrivilege, string password, CancellationToken cancellationToken) {
        try {
            using var live = await OpenAsync(connection, leastPrivilege, password, cancellationToken)
                .ConfigureAwait(false);
            return null;
        } catch (Exception e) {
            return e.Message;
        }
    }

    /// <summary>
    /// Opens a connection, reads something off it, and closes it — the object tree's
    /// path. Errors come back as a message rather than an exception for the same
    /// reason a failing query does: a database that refuses is an answer the tree has
    /// to show, not a fault of this server.
    /// </summary>
    public async Task<(T Value, string Error)> BrowseAsync<T>(
        StoredConnection connection, bool leastPrivilege, string password,
        Func<SqlConnection, CancellationToken, Task<T>> read, CancellationToken cancellationToken) {
        try {
            using var live = await OpenAsync(connection, leastPrivilege, password, cancellationToken)
                .ConfigureAwait(false);
            return (await read(live, cancellationToken).ConfigureAwait(false), null);
        } catch (Exception e) {
            _logger?.LogDebug("Browsing '{Connection}' failed: {Error}", connection.Name, e.Message);
            return (default, e.Message);
        }
    }

    /// <summary>
    /// Runs <paramref name="sql"/> and returns every result set it produced.
    /// <paramref name="queryId"/> is what <see cref="Cancel"/> names; it is registered
    /// for exactly as long as the command is in flight.
    /// </summary>
    public async Task<QueryResult> RunAsync(
        StoredConnection connection, string sql, bool leastPrivilege, Guid actor, string queryId,
        string password, CancellationToken cancellationToken) {
        var messages = new List<string>();
        var sets = new List<QueryResultSet>();
        var watch = Stopwatch.StartNew();
        var rowsAffected = 0;
        var canceled = false;
        string error = null;
        Active active = null;

        try {
            using var live = await OpenAsync(connection, leastPrivilege, password, cancellationToken)
                .ConfigureAwait(false);
            // PRINT and RAISERROR(…, 0, …) arrive here, not in the reader.
            live.InfoMessage += (_, e) => {
                foreach (SqlError message in e.Errors) {
                    messages.Add(message.Message);
                }
            };

            using var command = new SqlCommand(sql, live) {
                CommandTimeout = connection.TimeoutSeconds,
            };
            active = new Active(actor, command);
            _running[queryId] = active;
            try {
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                do {
                    if (reader.FieldCount == 0) {
                        continue; // a statement that returned no grid; its count is below
                    }
                    sets.Add(await ReadSetAsync(reader, connection.RowCap, cancellationToken)
                        .ConfigureAwait(false));
                } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                rowsAffected = reader.RecordsAffected;
            } finally {
                _running.TryRemove(queryId, out _);
            }
        } catch (Exception e) when (WasCancelled(active, e, cancellationToken)) {
            canceled = true;
            error = "Cancelled.";
        } catch (SecretNotFoundException e) {
            error = e.Message;
        } catch (SqlException e) {
            // A failing statement is an answer, not a server fault: it belongs in the
            // Messages tab beside the row counts, the way SSMS shows it.
            error = e.Message;
            foreach (SqlError sqlError in e.Errors) {
                messages.Add(sqlError.Message);
            }
        } catch (Exception e) {
            _logger?.LogWarning("Query against '{Connection}' failed: {Error}", connection.Name, e.Message);
            error = e.Message;
        }

        watch.Stop();
        if (rowsAffected >= 0 && error == null) {
            messages.Add($"({rowsAffected} row{(rowsAffected == 1 ? "" : "s")} affected)");
        }
        return new QueryResult {
            ResultSets = sets,
            Messages = messages,
            RowsAffected = rowsAffected,
            ElapsedMs = watch.Elapsed.TotalMilliseconds,
            Canceled = canceled,
            Error = error,
        };
    }

    /// <summary>
    /// Stops a running query. Scoped to whoever started it: without the actor check
    /// this route is "cancel anybody's query by guessing an id".
    /// </summary>
    public bool Cancel(string queryId, Guid actor) {
        if (queryId == null || !_running.TryGetValue(queryId, out var active) || active.Actor != actor) {
            return false;
        }
        try {
            // Set first: the command can throw before Cancel() returns, and the
            // outcome is decided from this flag.
            active.Cancelled = true;
            active.Command.Cancel();
            return true;
        } catch (Exception e) {
            // The command finishing between the lookup and the call is the normal race.
            _logger?.LogDebug("Cancelling {QueryId} did nothing: {Error}", queryId, e.Message);
            return false;
        }
    }

    // --- the wire -----------------------------------------------------------

    private async Task<SqlConnection> OpenAsync(
        StoredConnection connection, bool leastPrivilege, string password, CancellationToken cancellationToken) {
        var spec = SpecFor(connection, leastPrivilege);
        // A prompt-every-session connection has no stored password by design, so the
        // one typed for this session is handed to the same resolver the spec expects.
        var secrets = _secrets;
        if (!string.IsNullOrEmpty(password)) {
            var supplied = new InMemorySecretProvider();
            supplied.Set(spec.EffectiveSecretRef, password);
            secrets = SecretStore.ForProviders(supplied);
        }
        var live = new SqlConnection(spec.BuildConnectionString(secrets));
        try {
            await live.OpenAsync(cancellationToken).ConfigureAwait(false);
            return live;
        } catch {
            live.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one grid, stopping at the cap.
    /// <para>
    /// <c>cap + 1</c> rows are read and the extra one is thrown away: getting it
    /// proves the result was truncated without a second <c>COUNT</c> query, which is
    /// the same trick <see cref="DisplayTable"/>'s <c>TotalRows = -1</c> encodes.
    /// </para>
    /// </summary>
    private static async Task<QueryResultSet> ReadSetAsync(
        SqlDataReader reader, int cap, CancellationToken cancellationToken) {
        var columns = new List<string>(reader.FieldCount);
        var types = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++) {
            var name = reader.GetName(i);
            columns.Add(string.IsNullOrEmpty(name) ? "(no column name)" : name);
            types.Add(DisplayTable.KindOf(reader.GetFieldType(i)));
        }

        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            if (rows.Count == cap) {
                truncated = true;
                break;
            }
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = DisplayTable.CellText(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            rows.Add(row);
        }
        return new QueryResultSet {
            Columns = columns,
            Types = types,
            Rows = rows,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Whether this failure is somebody's Cancel button rather than a failure.
    /// <para>
    /// Read from the flag the cancel route set, not inferred from the exception. A
    /// cancelled <c>SqlCommand</c> throws a <c>SqlException</c> whose first error is
    /// number 0 — and so does a server that is not listening, which would have
    /// recorded every unreachable database in the audit as "Cancelled".
    /// </para>
    /// </summary>
    private static bool WasCancelled(Active active, Exception e, CancellationToken cancellationToken) =>
        (active?.Cancelled ?? false)
        || e is OperationCanceledException
        || cancellationToken.IsCancellationRequested;
}
