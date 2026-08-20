using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// This test assembly IS the toy plugin: the same assembly-level exports a real
// ClrKernel.Language.X package ships.
[assembly: CellLanguageExport(typeof(ClrKernel.Language.UnitTest.ToyCellLanguage))]
[assembly: ConnectionProviderExport(typeof(ClrKernel.Language.UnitTest.ToyConnectionProvider))]

namespace ClrKernel.Language.UnitTest;

/// <summary>A minimal plugged-in cell language: enough to prove routing, selector
/// ordering, contribution application, and per-session isolation.</summary>
public sealed class ToyCellLanguage : ICellLanguage {
    public string Id => "toy";
    public string DisplayName => "Toy";
    public IReadOnlyList<string> Selectors { get; } = new[] { "#!toy", "#!toy-connect" };
    public IReadOnlyList<string> LanguageTags { get; } = new[] { "toy" };
    public ICellLanguageServices Services => null;
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution { get; } = new ScriptContribution(
        references: new[] { typeof(ToyMarker).Assembly },
        imports: new[] { "ClrKernel.Language.UnitTest" });

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) =>
        Task.FromResult<object>(cell.Selector + ":" + cell.Body.Trim());
}

/// <summary>Reachable from C# cells once the toy language's contribution applies.</summary>
public static class ToyMarker {
    public static int Value => 41;
}

public static class ToyConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = "Toy",
        DisplayName = "Toy",
        LanguageIds = new[] { "toy" },
        ConnectSelector = "#!toy-connect",
        Settings = new ConnectionSetting[] { new() { Name = "name", Required = true, DirectiveFlag = "--name" } },
    };
}

/// <summary>
/// Runtime plugins: an assembly loaded mid-session registers its cell languages
/// and connection providers with that session ONLY, exactly once, and the engine
/// announces the change.
/// </summary>
[TestClass]
public class PluginRegistrationTest {
    private static InteractiveScriptEngine NewEngine() =>
        new(Environment.CurrentDirectory, NullLogger.Instance);

    [TestMethod]
    public async Task A_registered_plugin_routes_cells_in_its_session_only() {
        var engineA = NewEngine();
        var engineB = NewEngine();

        var changes = 0;
        engineA.LanguagesChanged += () => changes++;
        Assert.IsTrue(engineA.RegisterPlugins(typeof(ToyCellLanguage).Assembly));
        Assert.AreEqual(1, changes);

        // Longest selector first still holds for a language added at run time.
        Assert.AreEqual("#!toy-connect:x", await engineA.ExecuteAsync("#!toy-connect\nx"));
        Assert.AreEqual("#!toy:hello", await engineA.ExecuteAsync("#!toy\nhello"));

        // The other session is untouched — the registry-of-factories rule.
        Assert.IsNull(engineB.Languages.ById("toy"));
        Assert.AreEqual(0, engineB.ConnectionProvidersFor("toy").Count());

        // The provider descriptor came along, per-language lookup included.
        Assert.AreEqual("Toy", engineA.ConnectionProvidersFor("toy").Single().Type);

        // And the descriptor list the session serves now includes the toy language.
        Assert.IsTrue(engineA.Languages.Describe().Any(d => d.Id == "toy"));
    }

    [TestMethod]
    public async Task The_plugin_contribution_reaches_csharp_cells() {
        var engine = NewEngine();
        engine.RegisterPlugins(typeof(ToyCellLanguage).Assembly);

        // Compiles only if the contribution's import landed on the live session.
        await engine.ExecuteAsync("var toyCheck = ToyMarker.Value;");
    }

    [TestMethod]
    public void Registration_is_idempotent_and_built_ins_are_skipped() {
        var engine = NewEngine();
        Assert.IsTrue(engine.RegisterPlugins(typeof(ToyCellLanguage).Assembly));
        Assert.IsFalse(engine.RegisterPlugins(typeof(ToyCellLanguage).Assembly), "second load is a no-op");
        Assert.AreEqual(1, engine.Languages.Languages.Count(l => l.Id == "toy"));
        Assert.AreEqual(1, engine.ConnectionProviders.Count(p => p.Type == "Toy"));

        // The shipped assemblies carry the same exports; re-registering one is a
        // no-op because its language Id and provider Type are already present.
        Assert.IsFalse(engine.RegisterPlugins(typeof(Sql.SqlCellLanguage).Assembly));
    }

    [TestMethod]
    public async Task A_hash_r_of_a_plugin_assembly_registers_it() {
        var engine = NewEngine();
        Assert.IsNull(engine.Languages.ById("toy"));

        await engine.ExecuteAsync($"#r \"{typeof(ToyCellLanguage).Assembly.Location}\"");

        Assert.IsNotNull(engine.Languages.ById("toy"), "the #r hook scans new assemblies for exports");
        Assert.AreEqual("#!toy:via-r", await engine.ExecuteAsync("#!toy\nvia-r"));
    }
}
