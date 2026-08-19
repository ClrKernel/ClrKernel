using System;
using System.IO;
using Microsoft.EntityFrameworkCore;

namespace ClrKernel.Jobs;

/// <summary>
/// Builds the run store named by <c>--store</c>: sqlite, sqlserver, postgres, or
/// files. The relational ones apply their migrations on startup so an empty
/// database becomes a usable one — which doubles as the reachability check.
/// </summary>
public static class RunStoreFactory {
    /// <summary>
    /// Retry schedule when <c>waitForDatabase</c> is set. docker-compose starts the
    /// app and its database together and the database is always slower, so serve
    /// waits ~30s before concluding the store is genuinely unreachable. Internal so
    /// tests can shrink it.
    /// </summary>
    internal static TimeSpan[] RetryDelays { get; set; } = {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15),
    };

    /// <param name="waitForDatabase">
    /// Retry an unreachable sqlserver/postgres for ~30s before failing (serve);
    /// one-shot commands fail immediately.
    /// </param>
    /// <param name="log">Where retry progress goes (stderr for the CLI).</param>
    public static IRunStore Create(JobsOptions options, bool waitForDatabase = false, Action<string> log = null) {
        var kind = (options.Store ?? "sqlite").ToLowerInvariant();
        if (kind == "files") {
            return new FileRunStore(options);
        }

        var store = new EfRunStore(ContextFactoryFor(kind, options));
        var delays = waitForDatabase && kind is "sqlserver" or "postgres" or "postgresql"
            ? RetryDelays
            : Array.Empty<TimeSpan>();

        for (var attempt = 0; ; attempt++) {
            try {
                store.Migrate();
                return store;
            } catch (Exception e) when (attempt < delays.Length) {
                log?.Invoke($"store '{kind}' not reachable yet ({FirstLine(e.Message)}); " +
                    $"retrying in {delays[attempt].TotalSeconds:0}s…");
                System.Threading.Thread.Sleep(delays[attempt]);
            } catch (Exception e) {
                throw new InvalidOperationException(
                    $"Could not open the {kind} run-history store: {FirstLine(e.Message)} " +
                    $"(store came from {options.SourceOf("store")}, connection string from " +
                    $"{options.SourceOf("connectionString")}). Fix the connection string or " +
                    "choose another --store.", e);
            }
        }
    }

    private static string FirstLine(string message) {
        var text = (message ?? string.Empty).Trim();
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd();
    }

    private static Func<RunsDbContext> ContextFactoryFor(string kind, JobsOptions options) {
        switch (kind) {
            case "sqlite": {
                    Directory.CreateDirectory(options.DataDir);
                    var sqlite = new DbContextOptionsBuilder<SqliteRunsDbContext>()
                        .UseSqlite(options.ConnectionString ?? $"Data Source={options.DefaultSqlitePath}")
                        .Options;
                    return () => new SqliteRunsDbContext(sqlite);
                }
            case "sqlserver": {
                    var sqlServer = new DbContextOptionsBuilder<SqlServerRunsDbContext>()
                        .UseSqlServer(Required(options.ConnectionString, kind))
                        .Options;
                    return () => new SqlServerRunsDbContext(sqlServer);
                }
            case "postgres":
            case "postgresql": {
                    var postgres = new DbContextOptionsBuilder<PostgresRunsDbContext>()
                        .UseNpgsql(Required(options.ConnectionString, kind))
                        .Options;
                    return () => new PostgresRunsDbContext(postgres);
                }
            default:
                throw new ArgumentException(
                    $"Unknown store '{kind}'. Expected sqlite, sqlserver, postgres, or files.");
        }
    }

    private static string Required(string connectionString, string kind) =>
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException(
                $"--store {kind} needs --connection-string (or CLRKERNEL_JOBS_CONNECTION). " +
                "Passwords belong in the connection string's integrated auth or a secret reference, " +
                "never checked into a notebook.");
}
