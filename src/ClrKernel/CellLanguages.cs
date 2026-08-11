using System;
using ClrKernel.AnalysisServices;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.Fabric;
using ClrKernel.Language.Http;
using ClrKernel.Language.Mermaid;
using ClrKernel.Language.PowerShell;
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
        CellLanguageRegistry.Default = new CellLanguageRegistry(new Func<ICellLanguage>[] {
            () => new HttpCellLanguage(),
            () => new MermaidCellLanguage(),
            () => new PowerShellCellLanguage(),
            () => new SqlCellLanguage(),
            () => new DaxCellLanguage(),
        });
        CellLanguageRegistry.DefaultContributions = new[] {
            // Fabric is reachable from C# cells (Fabric.Connect() -> warehouse
            // bulk-insert / reload-batch) but owns no cell magic.
            new ScriptContribution(
                references: new[] { typeof(FabricConnection).Assembly },
                imports: new[] { "ClrKernel.Database.Provider.Fabric" }),
        };
    }

}
