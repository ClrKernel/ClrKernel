using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// <c>#r "project: …"</c>: a local project is built with the SDK and its output
/// referenced. The parse and the framework choice are pure and tested as such;
/// everything else builds a real fixture, because the contract is "whatever
/// <c>dotnet build</c> produces" and only <c>dotnet build</c> can say what that is.
/// </summary>
[TestClass]
public class ProjectReferenceTest {
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "projects");

    /// <summary>
    /// A fresh copy of every fixture project. Copied rather than built in place,
    /// because three target-framework runs of this suite would otherwise share
    /// one <c>obj/</c> — and a test that edits a source file must not edit the
    /// fixture.
    /// </summary>
    private static string FreshFixtures() {
        var dir = Path.Combine(Path.GetTempPath(), "clrkernel-projects-test", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(_fixtures, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(dir, Path.GetRelativePath(_fixtures, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target);
        }
        return dir;
    }

    private static InteractiveScriptEngine EngineIn(string directory) {
        InteractiveScriptEngine.RefsFilePath = null;
        return new InteractiveScriptEngine(directory, NullLogger.Instance);
    }

    private static string Text(object result) => ((DisplayData)result).Data["text/plain"]?.ToString();

    // --- the parse ------------------------------------------------------------

    [TestMethod]
    public void Parses_the_path_and_options_and_refuses_what_it_does_not_know() {
        var dir = FreshFixtures();
        var project = Path.Combine(dir, "SimpleLib", "SimpleLib.csproj");

        var plain = ProjectReferenceRequest.TryParse("#r \"project: SimpleLib/SimpleLib.csproj\"", dir);
        Assert.AreEqual(project, plain.ProjectPath, "relative to the directory it was given");
        Assert.AreEqual("Debug", plain.Configuration);
        Assert.IsNull(plain.Framework);
        Assert.IsFalse(plain.NoBuild);

        var full = ProjectReferenceRequest.TryParse(
            $"#r \"project: {project}, configuration=Release, FRAMEWORK=net8.0, NoBuild=true\"", "/nowhere");
        Assert.AreEqual("Release", full.Configuration, "keys are case-insensitive");
        Assert.AreEqual("net8.0", full.Framework);
        Assert.IsTrue(full.NoBuild);

        Assert.IsNull(ProjectReferenceRequest.TryParse("#r \"nuget: Humanizer\"", dir), "not a project reference");
        Assert.IsNull(ProjectReferenceRequest.TryParse("var x = 1;", dir));

        var unknown = Assert.ThrowsExactly<ArgumentException>(() =>
            ProjectReferenceRequest.TryParse("#r \"project: SimpleLib/SimpleLib.csproj, Configuraton=Release\"", dir));
        StringAssert.Contains(unknown.Message, "Configuraton", "a misspelled option is an error, not Debug by accident");

        var solution = Assert.ThrowsExactly<ArgumentException>(() =>
            ProjectReferenceRequest.TryParse("#r \"project: All.sln\"", dir));
        StringAssert.Contains(solution.Message, "solution");

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            ProjectReferenceRequest.TryParse("#r \"project: Nope/Nope.csproj\"", dir));
    }

    [TestMethod]
    public void Picks_the_highest_framework_the_running_kernel_can_load() {
        Assert.AreEqual("net9.0", ProjectReferences.PickFramework(new[] { "net8.0", "net9.0" }, 10));
        Assert.AreEqual("net9.0", ProjectReferences.PickFramework(new[] { "net8.0", "net9.0" }, 9));
        Assert.AreEqual("net8.0", ProjectReferences.PickFramework(new[] { "net8.0", "net9.0" }, 8));
        Assert.IsNull(ProjectReferences.PickFramework(new[] { "net9.0" }, 8), "nothing loadable is null, not a guess");
        Assert.AreEqual("netstandard2.0", ProjectReferences.PickFramework(new[] { "netstandard2.0", "net11.0" }, 8),
            "netstandard always loads, and loses to any loadable netX");
        Assert.AreEqual("net8.0", ProjectReferences.PickFramework(new[] { "netstandard2.0", "net8.0" }, 8));
        Assert.IsNull(ProjectReferences.PickFramework(new[] { "net48" }, 8), "a Framework target never loads here");
    }

    // --- building -------------------------------------------------------------

    [TestMethod]
    public async Task A_local_project_is_built_and_its_types_are_usable() {
        var engine = EngineIn(FreshFixtures());
        await engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj\"");
        Assert.AreEqual("Hello, Ada!", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Greet(\"Ada\")")));
    }

    [TestMethod]
    public async Task A_multi_targeted_project_gets_the_framework_this_runtime_can_load() {
        var engine = EngineIn(FreshFixtures());
        await engine.ExecuteAsync("#r \"project: MultiTarget/MultiTarget.csproj\"");
        // The suite runs on net8, net9 and net10; the answer has to follow the
        // runtime rather than be written down.
        var expected = Environment.Version.Major >= 9 ? "net9.0" : "net8.0";
        Assert.AreEqual(expected, Text(await engine.ExecuteAsync("MultiTarget.Which.Framework")));
    }

    [TestMethod]
    public async Task A_project_reference_brings_the_project_it_references() {
        var engine = EngineIn(FreshFixtures());
        await engine.ExecuteAsync("#r \"project: WithP2P/WithP2P.csproj\"");
        Assert.AreEqual("HELLO, ADA!", Text(await engine.ExecuteAsync("WithP2P.Shouter.Shout(\"Ada\")")));
        Assert.AreEqual("Hello, Ada!", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Greet(\"Ada\")")),
            "the referenced project's types are usable too — MSBuild put its dll in the output");
    }

    [TestMethod]
    public async Task A_build_failure_surfaces_msbuilds_own_message() {
        var engine = EngineIn(FreshFixtures());
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            engine.ExecuteAsync("#r \"project: BuildFails/BuildFails.csproj\""));
        StringAssert.Contains(e.Message, "CS0029", "the compiler's error id, verbatim");
        StringAssert.Contains(e.Message, "Broken.cs", "and where it is");
    }

    [TestMethod]
    public async Task Rerunning_after_an_edit_replaces_the_code_and_keeps_the_session() {
        var dir = FreshFixtures();
        var engine = EngineIn(dir);
        await engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj\"");
        await engine.ExecuteAsync("var before = SimpleLib.Greeter.Greet(\"Ada\");");

        File.WriteAllText(Path.Combine(dir, "SimpleLib", "Greeter.cs"),
            "namespace SimpleLib;\npublic static class Greeter {\n"
            + "    public static string Greet(string name) => $\"Hi, {name}!\";\n"
            + "    public static string Wave() => \"o/\";\n}\n");

        await engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj\"");
        Assert.AreEqual("o/", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Wave()")), "the new method is there");
        Assert.AreEqual("Hi, Ada!", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Greet(\"Ada\")")),
            "and the changed one changed — the old build left, or this would be CS1703");
        Assert.AreEqual("Hello, Ada!", Text(await engine.ExecuteAsync("before")), "what earlier cells made is still here");
    }

    [TestMethod]
    public void An_unchanged_rebuild_is_not_a_reload() {
        var dir = FreshFixtures();
        var projects = new ProjectReferences(null);
        var request = ProjectReferenceRequest.TryParse("#r \"project: SimpleLib/SimpleLib.csproj\"", dir);

        var first = projects.Resolve(request);
        var second = projects.Resolve(request);

        Assert.IsTrue(first.Reloaded);
        Assert.IsFalse(second.Reloaded, "same source, same MVID — nothing to swap");
        Assert.AreEqual(first.AssemblyPath, second.AssemblyPath);
        Assert.AreEqual(1, Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(first.AssemblyPath))).Length,
            "the second build's directory is gone rather than left as an empty <n+1>");
    }

    [TestMethod]
    public async Task NoBuild_refuses_before_a_build_and_reuses_one_after() {
        var dir = FreshFixtures();
        var engine = EngineIn(dir);

        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj, NoBuild=true\""));
        StringAssert.Contains(e.Message, "NoBuild", "and it does not quietly build instead");

        // What a CI step, or a person in a terminal, would have done.
        var (code, output) = DotnetCli.Run(DotnetCli.Locate(),
            new[] { "build", Path.Combine(dir, "SimpleLib", "SimpleLib.csproj"), "--nologo", "-v:q", "-nodeReuse:false" },
            null, TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, code, output);

        await engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj, NoBuild=true\"");
        Assert.AreEqual("Hello, Ada!", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Greet(\"Ada\")")));
    }

    [TestMethod]
    public async Task A_relative_path_inside_an_import_resolves_against_the_imported_file() {
        var dir = FreshFixtures();
        // The library sits beside WithP2P and names SimpleLib as its sibling's
        // sibling — a path that means nothing from the notebook's own directory.
        var library = Path.Combine(dir, "WithP2P", "helpers.csx");
        File.WriteAllText(library, "#r \"project: ../SimpleLib/SimpleLib.csproj\"\n");

        var engine = EngineIn(Path.GetTempPath());
        await engine.ExecuteAsync($"#!import \"{library}\"");
        Assert.AreEqual("Hello, Ada!", Text(await engine.ExecuteAsync("SimpleLib.Greeter.Greet(\"Ada\")")));
    }

    // --- the fronts -----------------------------------------------------------

    [TestMethod]
    public async Task A_headless_run_fails_on_a_project_that_does_not_build() {
        var dir = FreshFixtures();
        var notebook = Path.Combine(dir, "run.nb.md");
        File.WriteAllText(notebook, "```csharp\n#r \"project: BuildFails/BuildFails.csproj\"\n```\n");
        InteractiveScriptEngine.RefsFilePath = null;

        var code = await NotebookRunner.RunAsync(RunnerOptions.Parse(new[] { notebook }), NullLoggerFactory.Instance);
        Assert.AreNotEqual(0, code, "a scheduled job must not report success over a build that failed");
    }

    [TestMethod]
    public async Task Completion_lists_the_projects_types_with_their_docs() {
        var engine = EngineIn(FreshFixtures());
        await engine.ExecuteAsync("#r \"project: SimpleLib/SimpleLib.csproj\"");

        var service = new ScriptLanguageService();
        const string code = "SimpleLib.Greeter.";
        var result = await service.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        Assert.IsTrue(result.Items.Any(i => i.Label == "Greet"), "the project's public members complete: "
            + string.Join(", ", result.Items.Select(i => i.Label).Take(20)));

        // The fixture emits its XML docs beside the dll, which is where the
        // language service looks — so a project's /// summaries show like a package's.
        const string call = "SimpleLib.Greeter.Greet(\"Ada\")";
        var hover = await service.GetHoverAsync(engine.SnapshotState(), call, call.IndexOf("Greet(") + 1);
        Assert.IsNotNull(hover);
        StringAssert.Contains(hover.Documentation ?? string.Empty, "greeting for a name");
    }
}
