using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// Drives the executor's cell loop against a scripted fake kernel over an in-memory
/// duplex stream — the same Content-Length JSON-RPC framing `clrkernel serve` speaks,
/// no child process needed.
/// </summary>
[TestClass]
public class JobExecutorTest {
    private string _dir;
    private EfRunStore _store;
    private JobsOptions _options;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-executor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _options = new JobsOptions { DataDir = _dir, NotebooksRoot = _dir };
        _store = EfRunStore.Sqlite(Path.Combine(_dir, "test.db"));
        _store.Migrate();
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Speaks the serve protocol: ok cells return 42, "throw" cells error, "Display" cells notify.</summary>
    private sealed class FakeKernel {
        public JsonRpc Rpc { get; set; }

        [JsonRpcMethod("initialize")]
        public object Initialize() => new { name = "fake-kernel", version = "9.9.9" };

        [JsonRpcMethod("execute")]
        public async Task<object> Execute(string cellId, string code) {
            if (code.Contains("Display(")) {
                await Rpc.NotifyWithParameterObjectAsync("display",
                    new { cellId, data = new Dictionary<string, object> { ["text/plain"] = "displayed!" } });
            }
            if (code.Contains("throw")) {
                return new {
                    cellId,
                    status = "error",
                    error = new { name = "BoomException", message = "kaboom", stack = "at A\nat B" },
                };
            }
            return new { cellId, status = "ok", data = new Dictionary<string, object> { ["text/plain"] = "42" } };
        }

        [JsonRpcMethod("shutdown")]
        public void Shutdown() { }
    }

    private async Task<(Run Run, string Artifact)> RunNotebookAsync(string notebook, Dictionary<string, object> parameters = null) {
        var notebookPath = Path.Combine(_dir, "nb.nb.md");
        File.WriteAllText(notebookPath, notebook);
        var job = new JobDefinition {
            Name = "test-job",
            NotebookPath = notebookPath,
            NotebookRelative = "nb.nb.md",
            Parameters = parameters ?? new Dictionary<string, object>(),
        };

        var executor = new JobExecutor(_store, _options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var plan = executor.BuildPlan(job);
        var run = await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            JobName = job.Name,
            NotebookPath = job.NotebookRelative,
            Status = RunStatus.Running,
            Trigger = RunTrigger.Manual,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
        });
        var cells = JobExecutor.SeedCells(run.Id, plan);
        await _store.SaveCellsAsync(run.Id, cells);

        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var fake = new FakeKernel();
        using var serverRpc = JsonRpc.Attach(serverStream, fake);
        fake.Rpc = serverRpc;

        var artifact = Path.Combine(_dir, "out.ipynb");
        using var client = new KernelClient(clientStream, clientStream);
        await executor.ExecuteCellsAsync(client, run, plan, cells, artifact, _ => { }, CancellationToken.None);
        return (run, artifact);
    }

    [TestMethod]
    public async Task A_notebook_runs_cell_by_cell_and_writes_the_artifact() {
        var (run, artifact) = await RunNotebookAsync(
            """
            # Title

            ```csharp
            var x = 1;
            ```

            ```csharp
            Display("hi")
            ```
            """);

        Assert.AreEqual(RunStatus.Succeeded, run.Status);
        var cells = await _store.GetCellsAsync(run.Id);
        Assert.AreEqual(2, cells.Count);
        Assert.IsTrue(cells.All(c => c.Status == CellStatus.Succeeded));
        Assert.IsTrue(cells.All(c => c.StartedAt != null && c.FinishedAt != null));

        var json = File.ReadAllText(artifact);
        StringAssert.Contains(json, "\"42\"", "execute_result mime bundle");
        StringAssert.Contains(json, "displayed!", "display notification captured");
        StringAssert.Contains(json, "# Title", "markdown passes through");
    }

    [TestMethod]
    public async Task A_failing_cell_stops_the_run_and_skips_the_rest() {
        var (run, artifact) = await RunNotebookAsync(
            """
            ```csharp
            var ok = true;
            ```

            ```csharp
            throw new Exception();
            ```

            ```csharp
            var never = 1;
            ```
            """);

        Assert.AreEqual(RunStatus.Failed, run.Status);
        StringAssert.Contains(run.ErrorSummary, "kaboom");

        var cells = await _store.GetCellsAsync(run.Id);
        Assert.AreEqual(CellStatus.Succeeded, cells[0].Status);
        Assert.AreEqual(CellStatus.Failed, cells[1].Status);
        Assert.AreEqual(CellStatus.Skipped, cells[2].Status);

        var json = File.ReadAllText(artifact);
        StringAssert.Contains(json, "BoomException", "error output written");
        StringAssert.Contains(json, "var never = 1;", "skipped cells still written, unexecuted");
    }

    [TestMethod]
    public async Task Parameters_are_injected_as_the_first_cell_and_tagged() {
        var (run, artifact) = await RunNotebookAsync(
            """
            ```csharp
            var x = who;
            ```
            """,
            new Dictionary<string, object> { ["who"] = "world", ["count"] = 5 });

        Assert.AreEqual(RunStatus.Succeeded, run.Status);
        var cells = await _store.GetCellsAsync(run.Id);
        Assert.AreEqual(2, cells.Count, "injected parameters cell + the notebook cell");

        var json = File.ReadAllText(artifact);
        StringAssert.Contains(json, "injected-parameters");
        // System.Text.Json writes '"' as " inside the cell source.
        StringAssert.Contains(json, "var who = \\u0022world\\u0022;");
        StringAssert.Contains(json, "var count = 5;");
    }

    [TestMethod]
    public void The_parameters_cell_lands_after_a_parameters_marker_cell() {
        var notebookPath = Path.Combine(_dir, "marker.nb.md");
        File.WriteAllText(notebookPath,
            """
            ```csharp
            // parameters
            var who = "default";
            ```

            ```csharp
            who
            ```
            """);
        var executor = new JobExecutor(_store, _options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var plan = executor.BuildPlan(new JobDefinition {
            Name = "m",
            NotebookPath = notebookPath,
            Parameters = new Dictionary<string, object> { ["who"] = "override" },
        });

        var code = plan.Where(p => p.CodeIndex >= 0).ToList();
        Assert.AreEqual(3, code.Count);
        StringAssert.Contains(code[0].Cell.Source, "// parameters");
        Assert.IsTrue(code[1].Injected, "injected cell right after the marker");
        StringAssert.Contains(code[1].Cell.Source, "var who = \"override\";");
    }

    [TestMethod]
    public void Scalar_parameters_keep_their_types() {
        var cell = JobExecutor.RenderParametersCell(new Dictionary<string, object> {
            ["count"] = 5,
            ["rate"] = 0.5,
            ["on"] = true,
            ["name"] = "abc",
        });
        StringAssert.Contains(cell, "var count = 5;");
        StringAssert.Contains(cell, "var rate = 0.5;");
        StringAssert.Contains(cell, "var on = true;");
        StringAssert.Contains(cell, "var name = \"abc\";");
    }
}
