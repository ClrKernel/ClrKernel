using System;
using ClrKernel.AnalysisServices;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Http;
using ClrKernel.Language.Mermaid;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Test-run composition root. Engines built as <c>new InteractiveScriptEngine(dir,
/// logger)</c> take <see cref="CellLanguageRegistry.Default"/>, so the languages
/// have to be registered once before any test constructs one — the same job
/// <c>Program.Main</c> does for the real kernel.
/// </summary>
[TestClass]
public static class TestCellLanguages {
    [AssemblyInitialize]
    public static void Register(TestContext context) {
        CellLanguageRegistry.Default = new CellLanguageRegistry(new Func<ICellLanguage>[] {
            () => new HttpCellLanguage(),
            () => new MermaidCellLanguage(),
            () => new PowerShellCellLanguage(),
            () => new SqlCellLanguage(),
            () => new DaxCellLanguage(),
        });
        // Mirrors Program.Main: providers with no #! selector still contribute
        // to the C# scripting session.
        CellLanguageRegistry.DefaultContributions = new[] {
            new ScriptContribution(
                references: new[] { typeof(ClrKernel.Database.Provider.Fabric.FabricConnection).Assembly },
                imports: new[] { "ClrKernel.Database.Provider.Fabric" }),
        };
    }
}
