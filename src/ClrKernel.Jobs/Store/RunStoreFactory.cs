using System;
using System.IO;
using Microsoft.EntityFrameworkCore;

namespace ClrKernel.Jobs;

/// <summary>
/// Builds the run store named by <c>--store</c>: sqlite (default, zero config),
/// sqlserver, postgres, or files. The relational ones apply their migrations on
/// startup so an empty database becomes a usable one.
/// </summary>
public static class RunStoreFactory {
    public static IRunStore Create(JobsOptions options) {
        var kind = (options.Store ?? "sqlite").ToLowerInvariant();
        if (kind == "files") {
            return new FileRunStore(options);
        }

        var store = new EfRunStore(ContextFactoryFor(kind, options));
        store.Migrate();
        return store;
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
