using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A library referenced by path from its build output: the folder holds the
/// library and its .deps.json but none of its NuGet dependencies, which live in
/// the NuGet cache. The kernel has to find them there, or the cell fails with
/// FileNotFoundException the first time it touches the library.
/// </summary>
[TestClass]
public class PackageDependencyTest {
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "clrkernel-deps-" + Guid.NewGuid().ToString("N"));

    private static string Emit(string directory, string name, string source, params string[] references) {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        MetadataReference[] refs = [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            .. Array.ConvertAll(references, r => MetadataReference.CreateFromFile(r)),
        ];
        var emit = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path);
        Assert.IsTrue(emit.Success, string.Join("\n", emit.Diagnostics));
        return path;
    }

    private const string _depsJson = """
        {
          "targets": { ".NETCoreApp,Version=v8.0": {
            "Lib/1.0.0": { "dependencies": { "Their.Dep": "2.0.0" }, "runtime": { "Lib.dll": {} } },
            "Their.Dep/2.0.0": { "runtime": { "lib/net8.0/Their.Dep.dll": {} } }
          } },
          "libraries": {
            "Lib/1.0.0": { "type": "project", "path": "" },
            "Their.Dep/2.0.0": { "type": "package", "path": "their.dep/2.0.0" }
          }
        }
        """;

    [TestMethod]
    public void The_deps_json_names_the_package_file_in_the_cache() {
        var library = Path.Combine(_root, "names", "Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(library));
        File.WriteAllText(Path.ChangeExtension(library, ".deps.json"), _depsJson);
        Assert.AreEqual(Path.Combine("/cache", "their.dep/2.0.0", "lib/net8.0/Their.Dep.dll"),
            InteractiveScriptEngine.FromDepsJson(library, "Their.Dep.dll", "/cache"));
        Assert.IsNull(InteractiveScriptEngine.FromDepsJson(library, "Lib.dll", "/cache"), "a project is not in the cache");
        Assert.IsNull(InteractiveScriptEngine.FromDepsJson(library, "Other.dll", "/cache"));
    }

    [TestMethod]
    public async Task A_build_output_library_finds_its_package_dependency_in_the_cache() {
        var cache = Path.Combine(_root, "cache");
        var dep = Emit(Path.Combine(cache, "their.dep", "2.0.0", "lib", "net8.0"), "Their.Dep",
            "namespace Their { public static class Dep { public static string Hello() => \"from the cache\"; } }");
        var lib = Emit(Path.Combine(_root, "bin"), "Lib",
            "namespace Their { public static class Lib { public static string Call() => Dep.Hello(); } }", dep);
        File.WriteAllText(Path.ChangeExtension(lib, ".deps.json"), _depsJson);

        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", cache);
        try {
            InteractiveScriptEngine.RefsFilePath = null;
            var engine = new InteractiveScriptEngine(Path.GetTempPath(), NullLogger.Instance);
            var result = (DisplayData)await engine.ExecuteAsync($"#r \"{lib}\"\nTheir.Lib.Call()");
            Assert.AreEqual("from the cache", result.Data["text/plain"]?.ToString());
        } finally {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }
}
