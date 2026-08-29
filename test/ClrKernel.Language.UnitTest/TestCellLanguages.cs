using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Dax;
using ClrKernel.Language.Http;
using ClrKernel.Language.Mermaid;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Shell;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Test-run composition root for the cell-language suite — the same job <c>Program.Main</c>
/// does for the real kernel, so selector dispatch is exercised against the real set.
/// <para>
/// <see cref="CellLanguageRegistry.Default"/> is <c>Empty</c> until something registers, so an
/// assembly that skips this still runs C# cells — it just has no <c>#!</c> languages. That is why
/// <c>ClrKernel.Core.UnitTest</c> has no registration and no <c>Language.*</c> reference at all.
/// </para>
/// </summary>
[TestClass]
public static class TestCellLanguages {
    [AssemblyInitialize]
    public static void Register(TestContext context) {
        // Languages emit display concepts; without the render registrations their
        // output has no text/html at all, so the suite mirrors this too.
        ClrKernel.Formatting.Html.HtmlFormatters.RegisterDefaults();
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
        ConnectionProviderRegistry.Default = new[] {
            ClrKernel.Database.Provider.SqlServer.SqlServerConnectionProvider.Descriptor,
            ClrKernel.Database.Provider.AnalysisServices.SsasConnectionProvider.Descriptor,
            ClrKernel.Language.Shell.SshConnectionProvider.Descriptor,
            ClrKernel.Language.PowerShell.PwshConnectionProvider.Descriptor,
        };
    }
}
