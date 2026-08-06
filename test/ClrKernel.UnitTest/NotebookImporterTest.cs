using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Script;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel;

[TestClass]
public class NotebookImporterTest {
    // --- directive parsing ---

    [TestMethod]
    public void ParsesQuotedPath() {
        Assert.IsTrue(NotebookImporter.TryParseDirective("#!import \"../../lib/lib.dib\"", out var path, out var force));
        Assert.AreEqual("../../lib/lib.dib", path);
        Assert.IsFalse(force);
    }

    [TestMethod]
    public void ParsesUnquotedPathAndForce() {
        Assert.IsTrue(NotebookImporter.TryParseDirective("  #!import --force lib/util.csx  ", out var path, out var force));
        Assert.AreEqual("lib/util.csx", path);
        Assert.IsTrue(force);

        Assert.IsTrue(NotebookImporter.TryParseDirective("#!import \"a b/c.dib\" --force", out path, out force));
        Assert.AreEqual("a b/c.dib", path);
        Assert.IsTrue(force);
    }

    [TestMethod]
    public void RejectsNonDirectiveLines() {
        Assert.IsFalse(NotebookImporter.TryParseDirective("var x = 1; // #!import \"x.dib\"", out _, out _));
        Assert.IsFalse(NotebookImporter.TryParseDirective("#!importx \"x.dib\"", out _, out _));
        Assert.IsFalse(NotebookImporter.TryParseDirective("#!import", out _, out _));
    }

    [TestMethod]
    public void ParsesLibAliasAndRegister() {
        Assert.IsTrue(NotebookImporter.TryParseDirective("#!lib \"shared://html.dib\"", out var path, out _));
        Assert.AreEqual("shared://html.dib", path);

        Assert.IsTrue(NotebookImporter.TryParseRegister("#!lib --register \"shared\" \"../../lib/shared\"", out var name, out var basePath));
        Assert.AreEqual("shared", name);
        Assert.AreEqual("../../lib/shared", basePath);

        Assert.IsTrue(NotebookImporter.TryParseRegister("#!import --register shared ../lib", out name, out basePath));
        Assert.AreEqual("shared", name);
        Assert.AreEqual("../lib", basePath);

        // a register line must not parse as a plain import
        Assert.IsFalse(NotebookImporter.TryParseDirective("#!lib --register \"shared\" \"../../lib/shared\"", out _, out _));
    }

    [TestMethod]
    public void PrefixResolvesAgainstRegisteredBase() {
        var root = MakeTempDir();
        var shared = Directory.CreateDirectory(Path.Combine(root, "lib", "shared")).FullName;

        var importer = new NotebookImporter();
        importer.RegisterPrefix("shared", shared);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(shared, "html.dib")),
            importer.ResolvePath("shared://html.dib"));

        var ex = Assert.ThrowsException<ArgumentException>(() => importer.ResolvePath("nope://x.dib"));
        StringAssert.Contains(ex.Message, "unknown prefix");
    }

    // --- .dib parsing ---

    [TestMethod]
    public void DibKeepsCSharpSectionsSkipsOthers() {
        var content = string.Join("\n",
            "#!csharp", "var a = 1;",
            "#!markdown", "# heading, not code",
            "#!csharp", "var b = 2;",
            "#!pwsh", "Get-ChildItem");
        var blocks = NotebookImporter.ParseDib(content);
        CollectionAssert.AreEqual(new[] { "var a = 1;", "var b = 2;" }, blocks.ToArray());
    }

    [TestMethod]
    public void DibKeepsNestedDirectivesInsideSections() {
        var content = string.Join("\n",
            "#!csharp", "#!import \"nested.dib\"", "var a = 1;");
        var blocks = NotebookImporter.ParseDib(content);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.Contains(blocks[0], "#!import \"nested.dib\"");
        StringAssert.Contains(blocks[0], "var a = 1;");
    }

    [TestMethod]
    public void DibSkipsMetaSection() {
        // VS Code saves .dib files with a #!meta JSON header; it must not compile as C#.
        var content = string.Join("\n",
            "#!meta", "", "{\"kernelInfo\":{\"defaultKernelName\":\"csharp\",\"items\":[]}}", "",
            "#!csharp", "var a = 1;");
        var blocks = NotebookImporter.ParseDib(content);
        CollectionAssert.AreEqual(new[] { "var a = 1;" }, blocks.ToArray());
    }

    [TestMethod]
    public void DibLeadingContentDefaultsToCSharp() {
        var blocks = NotebookImporter.ParseDib("var early = true;\n#!markdown\nprose");
        CollectionAssert.AreEqual(new[] { "var early = true;" }, blocks.ToArray());
    }

    // --- .ipynb parsing ---

    [TestMethod]
    public void IpynbKeepsCodeCellsSkipsMarkdown() {
        var json = @"{""cells"":[
            {""cell_type"":""code"",""source"":[""var a = 1;\n"",""var b = 2;""]},
            {""cell_type"":""markdown"",""source"":[""# not code""]},
            {""cell_type"":""code"",""source"":""var c = 3;""}
        ]}";
        var blocks = NotebookImporter.ParseIpynb(json);
        CollectionAssert.AreEqual(new[] { "var a = 1;\nvar b = 2;", "var c = 3;" }, blocks.ToArray());
    }

    // --- resolution, run-once, nesting ---

    [TestMethod]
    public async Task RunOnceThenForceReruns() {
        var dir = MakeTempDir();
        var file = Path.Combine(dir, "lib.csx");
        File.WriteAllText(file, "var x = 1;");

        var importer = new NotebookImporter();
        var runs = 0;
        Task Count(string _) { runs++; return Task.CompletedTask; }

        Assert.IsTrue(await importer.ImportAsync(file, force: false, Count));
        Assert.IsFalse(await importer.ImportAsync(file, force: false, Count));
        Assert.AreEqual(1, runs);

        Assert.IsTrue(await importer.ImportAsync(file, force: true, Count));
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task NestedImportsResolveRelativeToImportingFile() {
        var root = MakeTempDir();
        var libDir = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
        File.WriteAllText(Path.Combine(libDir, "inner.csx"), "var inner = true;");
        File.WriteAllText(Path.Combine(libDir, "outer.dib"), "#!csharp\nSTART");

        var importer = new NotebookImporter();
        var seen = new System.Collections.Generic.List<string>();

        // Simulate the engine: when executing outer's block, resolve a nested
        // relative path — it must resolve against lib/, not the test's cwd.
        await importer.ImportAsync(Path.Combine(libDir, "outer.dib"), false, async block => {
            seen.Add(block);
            var nested = importer.ResolvePath("inner.csx");
            Assert.AreEqual(Path.GetFullPath(Path.Combine(libDir, "inner.csx")), nested);
            await importer.ImportAsync("inner.csx", false, b => { seen.Add(b); return Task.CompletedTask; });
        });

        CollectionAssert.AreEqual(new[] { "START", "var inner = true;" }, seen.ToArray());
        // After import completes the active path stack is restored.
        Assert.AreEqual(Environment.CurrentDirectory, importer.ActivePath);
    }

    [TestMethod]
    public async Task CircularImportTerminates() {
        var dir = MakeTempDir();
        var file = Path.Combine(dir, "self.csx");
        File.WriteAllText(file, "// pretend this file imports itself");

        var importer = new NotebookImporter();
        var runs = 0;
        // Simulate the self-import: the callback tries to import the same file again.
        await importer.ImportAsync(file, false, async _ => {
            runs++;
            Assert.IsFalse(await importer.ImportAsync(file, false, __ => Task.CompletedTask));
        });
        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    public async Task MissingFileThrowsFileNotFound() {
        var importer = new NotebookImporter();
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => importer.ImportAsync("does-not-exist.dib", false, _ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task UnsupportedExtensionThrows() {
        var dir = MakeTempDir();
        var file = Path.Combine(dir, "lib.txt");
        File.WriteAllText(file, "nope");
        var importer = new NotebookImporter();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => importer.ImportAsync(file, false, _ => Task.CompletedTask));
    }

    private static string MakeTempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "clrkernel-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
