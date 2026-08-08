using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Runner;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class NotebookDocumentTest {
    [TestMethod]
    public void ParseMarkdown_separates_prose_and_csharp_cells() {
        var md = "# Title\n\nSome prose.\n\n```csharp\nvar x = 1;\n```\n\nMore prose.\n";
        var cells = NotebookDocument.ParseMarkdown(md);

        Assert.AreEqual(3, cells.Count);
        Assert.AreEqual(CellKind.Markdown, cells[0].Kind);
        StringAssert.Contains(cells[0].Source, "# Title");
        Assert.AreEqual(CellKind.Code, cells[1].Kind);
        Assert.AreEqual("var x = 1;", cells[1].Source);
        Assert.AreEqual(CellKind.Markdown, cells[2].Kind);
    }

    [TestMethod]
    public void ParseMarkdown_leaves_non_csharp_fences_in_markdown() {
        var md = "```python\nprint(1)\n```\n";
        var cells = NotebookDocument.ParseMarkdown(md);

        Assert.AreEqual(1, cells.Count);
        Assert.AreEqual(CellKind.Markdown, cells[0].Kind);
        StringAssert.Contains(cells[0].Source, "python");
    }
}

[TestClass]
public class NotebookRunnerOutputTest {
    private static async Task<JsonNode> RunToIpynbAsync(string markdown, params string[] extraArgs) {
        var dir = Path.Combine(Path.GetTempPath(), "clrkernel-nbtest", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var input = Path.Combine(dir, "nb.nb.md");
        var output = Path.Combine(dir, "out.ipynb");
        File.WriteAllText(input, markdown);

        var args = new[] { input, "-o", output }.Concat(extraArgs).ToArray();
        var options = RunnerOptions.Parse(args);
        InteractiveScriptEngine.RefsFilePath = null;
        var code = await NotebookRunner.RunAsync(options, NullLoggerFactory.Instance);
        Assert.IsTrue(File.Exists(output), "output notebook was written");
        var nb = JsonNode.Parse(File.ReadAllText(output));
        nb["__exit"] = code;
        return nb;
    }

    [TestMethod]
    public async Task Writes_ipynb_with_stream_and_result_and_markdown() {
        var nb = await RunToIpynbAsync(
            "# H\n\n```csharp\nConsole.WriteLine(\"hi\");\n```\n\ntext\n\n```csharp\n1 + 2\n```\n");

        Assert.AreEqual(0, nb["__exit"].GetValue<int>());
        Assert.AreEqual(4, nb["nbformat"].GetValue<int>());
        var cells = nb["cells"].AsArray();

        var types = cells.Select(c => c["cell_type"].GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "markdown", "code", "markdown", "code" }, types);

        // First code cell: stdout stream "hi".
        var stream = cells[1]["outputs"].AsArray()[0];
        Assert.AreEqual("stream", stream["output_type"].GetValue<string>());
        StringAssert.Contains(stream["text"].GetValue<string>(), "hi");

        // Second code cell: execute_result 3.
        var result = cells[3]["outputs"].AsArray()[0];
        Assert.AreEqual("execute_result", result["output_type"].GetValue<string>());
        StringAssert.Contains(result["data"]["text/plain"].GetValue<string>(), "3");

        // Every cell has an id.
        Assert.IsTrue(cells.All(c => !string.IsNullOrEmpty(c["id"]?.GetValue<string>())));
    }

    [TestMethod]
    public async Task Injected_parameters_cell_is_tagged_and_overrides() {
        var nb = await RunToIpynbAsync(
            "```csharp\n// parameters\nvar n = 1;\n```\n\n```csharp\nConsole.WriteLine(n);\n```\n",
            "-p", "n", "7");

        var cells = nb["cells"].AsArray();
        var injected = cells.First(c =>
            c["metadata"]?["tags"]?.AsArray().Any(t => t.GetValue<string>() == "injected-parameters") == true);
        StringAssert.Contains(injected["source"].GetValue<string>(), "var n = 7;");

        // The consuming cell prints the overridden value.
        var printed = cells.Last()["outputs"].AsArray()[0]["text"].GetValue<string>();
        StringAssert.Contains(printed, "7");
    }

    [TestMethod]
    public async Task Failing_cell_records_error_and_returns_nonzero() {
        var nb = await RunToIpynbAsync(
            "```csharp\nthrow new System.Exception(\"boom\");\n```\n\n```csharp\n1\n```\n");

        Assert.AreEqual(1, nb["__exit"].GetValue<int>());
        var cells = nb["cells"].AsArray();
        var error = cells[0]["outputs"].AsArray()[0];
        Assert.AreEqual("error", error["output_type"].GetValue<string>());
        StringAssert.Contains(error["evalue"].GetValue<string>(), "boom");
        // The cell after the failure is present but unexecuted.
        Assert.IsNull(cells[1]["execution_count"]);
        Assert.AreEqual(0, cells[1]["outputs"].AsArray().Count);
    }
}
