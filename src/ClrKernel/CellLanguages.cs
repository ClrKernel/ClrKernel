using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.Fabric;
using ClrKernel.Language.Dax;
using ClrKernel.Language.Http;
using ClrKernel.Language.Mermaid;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Shell;
using ClrKernel.Language.Sql;

namespace ClrKernel;

/// <summary>
/// The composition root for cell languages. This is the only place that knows
/// the full set — <c>Core.Scripting</c> deliberately references none of them, so
/// adding a language means registering it here rather than editing the engine.
/// </summary>
public static class CellLanguages {
    /// <summary>
    /// Registers every language shipped in the kernel, plus the script
    /// contributions of providers that have no <c>#!</c> selector of their own.
    /// Called once at startup, before any engine is constructed.
    /// </summary>
    public static void RegisterDefaults() {
        CellLanguageRegistry.Default = new CellLanguageRegistry(new Func<IReadOnlyList<ICellLanguage>>[] {
            () => new[] { new HttpCellLanguage() },
            () => new[] { new MermaidCellLanguage() },
            () => new[] { new PowerShellCellLanguage() },
            () => new[] { new ShellCellLanguage() },
            // One family, one session: the three dialects share the notebook's
            // connections, so a name declared in any of them means the same
            // connection in all of them. Sharing has to happen inside the factory
            // call — a session that outlived its engine would leak one notebook's
            // connections into the next.
            () => {
                var sql = new SqlCellLanguage();
                return new ICellLanguage[] {
                    sql,
                    new OracleSqlCellLanguage(sql.Session),
                    new AnsiSqlCellLanguage(sql.Session),
                };
            },
            () => new[] { new DaxCellLanguage() },
        });
        CellLanguageRegistry.DefaultContributions = new[] {
            // Fabric is reachable from C# cells (Fabric.Connect() -> warehouse
            // bulk-insert / reload-batch) but owns no cell magic.
            new ScriptContribution(
                references: new[] { typeof(FabricConnection).Assembly },
                imports: new[] { "ClrKernel.Database.Provider.Fabric" }),
            // PostgreSQL, the same way and for a second reason: DataSourceCatalog
            // finds a provider by scanning *loaded* assemblies, and a project
            // reference nothing touches is not loaded. Naming the type here is what
            // brings it in, so `#!ansisql` on a Postgres connection opens it and
            // `Postgres.Connect(...)` works in a C# cell — both without a `#r`.
            new ScriptContribution(
                references: new[] { typeof(Database.Provider.Postgres.Postgres).Assembly },
                imports: new[] { "ClrKernel.Database.Provider.Postgres" }),
        };
        // The connection types shipped in the kernel. Opt-in providers
        // (Oracle/Odbc/Jdbc) register theirs when #r loads them into a session.
        ConnectionProviderRegistry.Default = new[] {
            Database.Provider.SqlServer.SqlServerConnectionProvider.Descriptor,
            Database.Provider.AnalysisServices.SsasConnectionProvider.Descriptor,
            Database.Provider.Fabric.FabricConnectionProvider.Descriptor,
            Database.Provider.Postgres.PostgresConnectionProvider.Descriptor,
            Language.Shell.SshConnectionProvider.Descriptor,
            Language.PowerShell.PwshConnectionProvider.Descriptor,
        };
    }

}
