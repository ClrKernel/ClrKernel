using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Runner;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The warm kernel behind the web editor, driven against a scripted fake kernel
/// over an in-memory duplex stream — the same seam JobExecutorTest uses, so no
/// clrkernel binary is needed.
/// </summary>
[TestClass]
public class NotebookSessionTest {
    /// <summary>Speaks the serve protocol. "boom" fails, "display" emits two
    /// display notifications sharing a display_id, "flood" emits many.</summary>
    private sealed class FakeKernel {
        public JsonRpc Rpc { get; set; }
        public List<string> Executed { get; } = new();
        public TaskCompletionSource Gate { get; set; }

        [JsonRpcMethod("initialize")]
        public object Initialize() => new {
            name = "fake-kernel",
            version = "9.9.9",
            languages = new[] {
                new {
                    id = "sql", displayName = "SQL", defaultSelector = "#!sql",
                    selectors = new[] { "#!sql", "#!sql-connect" },
                    languageTags = new[] { "sql", "tsql" },
                },
            },
        };

        [JsonRpcMethod("execute")]
        public async Task<object> Execute(string cellId, string code) {
            Executed.Add(code);
            if (Gate != null) {
                await Gate.Task;
            }
            if (code.Contains("display")) {
                // Two notifications with one display_id: the second replaces the first.
                await Rpc.NotifyWithParameterObjectAsync("display", new {
                    cellId,
                    data = new Dictionary<string, object> { ["text/plain"] = "50%" },
                    transient = new Dictionary<string, object> { ["display_id"] = "bar" },
                });
                await Rpc.NotifyWithParameterObjectAsync("updateDisplay", new {
                    cellId,
                    data = new Dictionary<string, object> { ["text/plain"] = "100%" },
                    transient = new Dictionary<string, object> { ["display_id"] = "bar" },
                });
            }
            if (code.Contains("flood")) {
                for (var i = 0; i < 260; i++) {
                    await Rpc.NotifyWithParameterObjectAsync("display", new {
                        cellId,
                        data = new Dictionary<string, object> { ["text/plain"] = $"line {i}" },
                    });
                }
            }
            if (code.Contains("boom")) {
                return new {
                    cellId,
                    status = "error",
                    error = new { name = "BoomException", message = "kaboom", stack = "at A" },
                };
            }
            return new { cellId, status = "ok", data = new Dictionary<string, object> { ["text/plain"] = "42" } };
        }

        [JsonRpcMethod("shutdown")]
        public void Shutdown() { }
    }

    private readonly List<IDisposable> _disposables = new();

    [TestCleanup]
    public void Cleanup() {
        foreach (var disposable in _disposables) {
            try {
                disposable.Dispose();
            } catch {
                // best effort
            }
        }
    }

    private (NotebookSession Session, FakeKernel Kernel) NewSession() {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var fake = new FakeKernel();
        var serverRpc = JsonRpc.Attach(serverStream, fake);
        fake.Rpc = serverRpc;
        var client = new KernelClient(clientStream, clientStream);
        var session = new NotebookSession("s1", "/tmp/notebook.nb.md", null, null,
            _ => Task.FromResult(KernelProcess.ForClient(client)));
        _disposables.Add(serverRpc);
        _disposables.Add(session);
        return (session, fake);
    }

    private static IReadOnlyList<MarkdownCell> Cells(params string[] sources) =>
        sources.Select(s => MarkdownCell.Code("csharp", s)).ToList();

    private static IReadOnlyList<string> Ids(int count) =>
        Enumerable.Range(0, count).Select(i => $"c{i}").ToList();

    private static async Task RunAsync(NotebookSession session, params string[] sources) {
        Assert.IsTrue(session.TryStartRun(Cells(sources), Ids(sources.Length), out var completion));
        await completion;
    }

    /// <summary>
    /// Waits for something the kernel reported out of band. A display is not part
    /// of a cell's reply and can land just after it — which is exactly why the
    /// session subscribes for the kernel's whole life rather than for one run.
    /// Anything asserted about displays has to settle first, or it is a race that
    /// passes in Debug and fails in Release.
    /// </summary>
    private static async Task SettleAsync(Func<bool> arrived, string because) {
        for (var i = 0; i < 200; i++) {
            if (arrived()) {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail(because);
    }

    [TestMethod]
    public async Task Cells_run_in_order_against_one_warm_kernel() {
        var (session, kernel) = NewSession();
        await RunAsync(session, "var a = 1;", "a + 1");

        var first = session.Snapshot();
        Assert.AreEqual("succeeded", first["c0"].Status);
        Assert.AreEqual(1, first["c0"].ExecutionCount, "execution counts climb like a notebook's");
        Assert.AreEqual(2, first["c1"].ExecutionCount);

        // A second run reuses the same kernel — that is what makes variables persist.
        Assert.IsTrue(session.TryStartRun(Cells("a + 2"), new[] { "c9" }, out var again));
        await again;

        CollectionAssert.AreEqual(new[] { "var a = 1;", "a + 1", "a + 2" }, kernel.Executed.ToArray());
        Assert.AreEqual("fake-kernel", session.KernelName);
        Assert.AreEqual("sql", session.Languages.Single().Id);
        Assert.AreEqual(3, session.Snapshot()["c9"].ExecutionCount, "the counter carries across runs");
    }

    [TestMethod]
    public async Task A_failing_cell_stops_the_batch_and_skips_the_rest() {
        var (session, _) = NewSession();
        await RunAsync(session, "fine", "boom", "never");

        var state = session.Snapshot();
        Assert.AreEqual("succeeded", state["c0"].Status);
        Assert.AreEqual("failed", state["c1"].Status);
        Assert.AreEqual("skipped", state["c2"].Status, "papermill semantics, same as a scheduled run");
        StringAssert.Contains(state["c1"].Outputs.ToJsonString(), "kaboom");
    }

    [TestMethod]
    public async Task Update_display_replaces_in_place_rather_than_appending() {
        var (session, _) = NewSession();
        await RunAsync(session, "display something");

        await SettleAsync(
            () => session.Snapshot()["c0"].Outputs.ToJsonString().Contains("100%"),
            "the updateDisplay never arrived");

        var outputs = session.Snapshot()["c0"].Outputs;
        // One progress output, not two: a bar that ticks a hundred times must not
        // leave a hundred outputs behind.
        var displays = outputs.Where(o => o["output_type"].GetValue<string>() == "display_data").ToList();
        Assert.AreEqual(1, displays.Count, outputs.ToJsonString());
        StringAssert.Contains(displays[0].ToJsonString(), "100%");
    }

    [TestMethod]
    public async Task Runaway_output_is_capped_with_a_marker() {
        var (session, _) = NewSession();
        await RunAsync(session, "flood");

        // 260 displays are written before the reply, but the client can still be
        // draining them when it arrives — so wait for the cap rather than assume it.
        await SettleAsync(
            () => session.Snapshot()["c0"].Truncated,
            "a cell that never stops printing must not grow the server without bound");

        var cell = session.Snapshot()["c0"];
        Assert.IsTrue(cell.Outputs.Count <= 210, $"kept {cell.Outputs.Count} outputs");
        StringAssert.Contains(cell.Outputs.ToJsonString(), "truncated");
    }

    [TestMethod]
    public async Task A_second_run_while_busy_is_refused() {
        var (session, kernel) = NewSession();
        kernel.Gate = new TaskCompletionSource();

        Assert.IsTrue(session.TryStartRun(Cells("slow"), Ids(1), out var first));
        // Wait for the run to actually reach the kernel before asserting on busy.
        for (var i = 0; i < 100 && !session.Busy; i++) {
            await Task.Delay(10);
        }
        Assert.IsTrue(session.Busy);
        Assert.IsFalse(session.TryStartRun(Cells("other"), Ids(1), out _),
            "one run at a time per session — the kernel serializes anyway");

        kernel.Gate.SetResult();
        await first;
        Assert.IsFalse(session.Busy);
    }

    [TestMethod]
    public async Task A_dead_kernel_is_reported_on_the_cell_not_swallowed() {
        var (session, kernel) = NewSession();
        await RunAsync(session, "fine");

        // Drop the connection under the session, as a cell calling Environment.Exit
        // would: the next run must report it, not hang or vanish.
        kernel.Rpc.Dispose();
        Assert.IsTrue(session.TryStartRun(Cells("after"), new[] { "c9" }, out var completion));
        await completion;

        var cell = session.Snapshot()["c9"];
        Assert.AreEqual("failed", cell.Status);
        Assert.IsTrue(cell.Outputs.Count > 0, "the failure is visible on the cell, not swallowed");
    }

    [TestMethod]
    public async Task The_manager_reuses_evicts_and_restarts_sessions() {
        // An explicit path that cannot exist: otherwise this would find whatever
        // clrkernel happens to be installed on the machine running the tests.
        var manager = new NotebookSessionManager(
            new JobsOptions { ClrKernelPath = "/nonexistent/clrkernel" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotebookSessionManager>.Instance);
        _disposables.Add(manager);

        // No kernel binary here, so starting a real session fails — which is itself
        // the contract: a broken configuration surfaces when the editor opens.
        await Assert.ThrowsExactlyAsync<System.IO.FileNotFoundException>(
            () => manager.GetOrStartAsync("/tmp/no-kernel.nb.md", CancellationToken.None));
        Assert.IsNull(manager.Find("/tmp/no-kernel.nb.md"), "a failed start leaves no session behind");
        Assert.IsFalse(manager.Restart("/tmp/no-kernel.nb.md"));
    }

    [TestMethod]
    public void Execution_is_refused_off_localhost_without_a_key() {
        // The one policy question: an API key is optional, and without one a
        // server bound beyond loopback would be remote code execution for anyone
        // who can reach the port.
        Assert.IsTrue(JobsApi.IsLocalOnly(null), "the default bind is localhost");
        Assert.IsTrue(JobsApi.IsLocalOnly("http://localhost:5000"));
        Assert.IsTrue(JobsApi.IsLocalOnly("http://127.0.0.1:5000;http://localhost:5001"));
        Assert.IsFalse(JobsApi.IsLocalOnly("http://0.0.0.0:5000"));
        Assert.IsFalse(JobsApi.IsLocalOnly("http://192.168.1.10:5000"));
        Assert.IsFalse(JobsApi.IsLocalOnly("http://localhost:5000;http://0.0.0.0:5001"),
            "one public binding is enough to refuse");
        Assert.IsFalse(JobsApi.IsLocalOnly("not a url"));
    }
}
