using System;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.Fabric;
using ClrKernel.Language.Dax;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Registers only what the provider suite actually drives: several tests run
/// <c>SqlServer.Connection(...)</c> or <c>AnalysisServices.Connect(...)</c> inside a real
/// <c>#!csharp</c> cell, which needs those languages present. Http/Mermaid/PowerShell are not
/// registered here — that keeps this assembly off three packages it never exercises.
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
        // Providers emit display concepts; without the render registrations their
        // output has no text/html at all, so the suite mirrors this too.
        ClrKernel.Formatting.Html.HtmlFormatters.RegisterDefaults();
        CellLanguageRegistry.Default = new CellLanguageRegistry(new Func<ICellLanguage>[] {
            () => new SqlCellLanguage(),
            () => new DaxCellLanguage(),
        });
        // Fabric owns no #! selector but is still reachable from C# cells.
        CellLanguageRegistry.DefaultContributions = new[] {
            new ScriptContribution(
                references: new[] { typeof(FabricConnection).Assembly },
                imports: new[] { "ClrKernel.Database.Provider.Fabric" }),
        };
        ConnectionProviderRegistry.Default = new[] {
            ClrKernel.Database.Provider.SqlServer.SqlServerConnectionProvider.Descriptor,
            ClrKernel.Database.Provider.AnalysisServices.SsasConnectionProvider.Descriptor,
            ClrKernel.Database.Provider.Fabric.FabricConnectionProvider.Descriptor,
        };
    }
}
