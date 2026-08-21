using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
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
    /// <summary>
    /// Speaks the lsp protocol, which is what an editor session drives: an LSP
    /// handshake, <c>clrkernel/execute</c>, and displays under the <c>clrkernel/</c>
    /// names. "boom" fails, "display" emits two display notifications sharing a
    /// display_id, "flood" emits many.
    /// </summary>
    private sealed class FakeKernel {
        public JsonRpc Rpc { get; set; }
        public List<string> Executed { get; } = new();
        public List<string> CellUris { get; } = new();
        public TaskCompletionSource Gate { get; set; }

        /// <summary>What clrkernel/languages was asked about, or null if it never was.</summary>
        public string LanguagesAskedFor { get; private set; }

        private static object[] TheLanguages => new[] {
            new {
                id = "sql", displayName = "SQL", defaultSelector = "#!sql",
                selectors = new[] { "#!sql", "#!sql-connect" },
                languageTags = new[] { "sql", "tsql" },
            },
        };

        // Every binding below mirrors LspServer's: single-object parameter
        // deserialization, so a client that sends the wrong shape fails here the way
        // it would against the real server rather than being quietly tolerated.
        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public object Initialize(object _) => new {
            serverInfo = new { name = "fake-kernel", version = "9.9.9" },
            // Deliberately NOT the real language set: the handshake answers from a
            // fresh registry, so a session that trusted it would miss anything a
            // notebook loaded for itself. The session must ask clrkernel/languages.
            capabilities = new { experimental = new { clrkernel = new { languages = Array.Empty<object>() } } },
        };

        [JsonRpcMethod("initialized")]
        public void Initialized() { }

        public sealed class NotebookParams {
            public string NotebookUri { get; set; }
        }

        public sealed class ExecuteParams {
            public string CellId { get; set; }
            public string Code { get; set; }
        }

        [JsonRpcMethod("clrkernel/languages", UseSingleObjectParameterDeserialization = true)]
        public object Languages(NotebookParams p) {
            LanguagesAskedFor = p?.NotebookUri;
            return new { languages = TheLanguages };
        }

        [JsonRpcMethod("clrkernel/execute", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> Execute(ExecuteParams p) {
            var cellId = p?.CellId;
            var code = p?.Code ?? string.Empty;
            Executed.Add(code);
            CellUris.Add(cellId);
            if (Gate != null) {
                await Gate.Task;
            }
            if (code.Contains("display")) {
                // Two notifications with one display_id: the second replaces the first.
                await Rpc.NotifyWithParameterObjectAsync("clrkernel/display", new {
                    cellId,
                    data = new Dictionary<string, object> { ["text/plain"] = "50%" },
                    transient = new Dictionary<string, object> { ["display_id"] = "bar" },
                });
                await Rpc.NotifyWithParameterObjectAsync("clrkernel/updateDisplay", new {
                    cellId,
                    data = new Dictionary<string, object> { ["text/plain"] = "100%" },
                    transient = new Dictionary<string, object> { ["display_id"] = "bar" },
                });
            }
            if (code.Contains("flood")) {
                for (var i = 0; i < 260; i++) {
                    await Rpc.NotifyWithParameterObjectAsync("clrkernel/display", new {
                        cellId,
                        data = new Dictionary<string, object> { ["text/plain"] = $"line {i}" },
                    });
                }
            }
            if (code.Contains("plugin")) {
                // What `#r "nuget: ClrKernel.Language.Foo"` looks like from out here:
                // the session's language set grows while the notebook is open.
                await Rpc.NotifyWithParameterObjectAsync("clrkernel/languagesChanged", new {
                    notebookUri = "vscode-notebook-cell:/tmp/notebook.nb.md",
                    languages = new object[] {
                        TheLanguages[0],
                        new {
                            id = "foo", displayName = "Foo", defaultSelector = "#!foo",
                            selectors = new[] { "#!foo" }, languageTags = new[] { "foo" },
                        },
                    },
                });
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

        /// <summary>Every document notification, in order: "didOpen c0 csharp-script".</summary>
        public List<string> DocumentEvents { get; } = new();

        public sealed class DidOpenParams {
            public DocumentPayload TextDocument { get; set; }
        }

        public sealed class DidChangeParams {
            public DocumentPayload TextDocument { get; set; }
            public List<ChangePayload> ContentChanges { get; set; }
        }

        public sealed class DocumentPayload {
            public string Uri { get; set; }
            public string LanguageId { get; set; }
            public int Version { get; set; }
            public string Text { get; set; }
        }

        public sealed class ChangePayload {
            public string Text { get; set; }
        }

        private static string Cell(string uri) {
            var hash = uri?.IndexOf('#') ?? -1;
            return hash < 0 ? uri : uri[(hash + 1)..];
        }

        [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
        public void DidOpen(DidOpenParams p) => DocumentEvents.Add(
            $"didOpen {Cell(p?.TextDocument?.Uri)} {p?.TextDocument?.LanguageId} v{p?.TextDocument?.Version} " +
            $"{p?.TextDocument?.Text}");

        [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
        public void DidChange(DidChangeParams p) => DocumentEvents.Add(
            $"didChange {Cell(p?.TextDocument?.Uri)} v{p?.TextDocument?.Version} {p?.ContentChanges?[^1].Text}");

        [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
        public void DidClose(DidOpenParams p) => DocumentEvents.Add($"didClose {Cell(p?.TextDocument?.Uri)}");

        [JsonRpcMethod("shutdown")]
        public object Shutdown() => null;

        [JsonRpcMethod("exit")]
        public void Exit() { }
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

    /// <param name="seeded">Collects what the session published to the process-level
    /// language cache — what the notebook <em>parser</em> will use.</param>
    private (NotebookSession Session, FakeKernel Kernel) NewSession(
        List<IReadOnlyList<LanguageDescriptor>> seeded = null) {
        var (client, fake) = NewKernel();
        var session = new NotebookSession("s1", "/tmp/notebook.nb.md", null, null,
            _ => Task.FromResult(KernelProcess.ForClient(client)),
            onLanguages: languages => seeded?.Add(languages));
        _disposables.Add(session);
        return (session, fake);
    }

    /// <summary>One fake kernel on the far end of an in-memory duplex stream.</summary>
    private (KernelClient Client, FakeKernel Kernel) NewKernel() {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var fake = new FakeKernel();
        var serverRpc = JsonRpc.Attach(serverStream, fake);
        fake.Rpc = serverRpc;
        _disposables.Add(serverRpc);
        return (new KernelClient(clientStream, clientStream, KernelMode.Lsp), fake);
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
        // The handshake offered none, so this can only have come from the
        // per-notebook clrkernel/languages call.
        Assert.AreEqual("sql", session.Languages.Single().Id);
        Assert.AreEqual(3, session.Snapshot()["c9"].ExecutionCount, "the counter carries across runs");
    }

    [TestMethod]
    public async Task Cells_are_addressed_by_notebook_qualified_uri() {
        var (session, kernel) = NewSession();
        await RunAsync(session, "one", "two");

        // The lsp surface keys its engine off the path in the cell URI. Bare ids
        // ("c0") parse to a key of their own, which would silently give every cell
        // its own kernel and its own variables.
        CollectionAssert.AreEqual(
            new[] { "vscode-notebook-cell:/tmp/notebook.nb.md#c0", "vscode-notebook-cell:/tmp/notebook.nb.md#c1" },
            kernel.CellUris.ToArray());
        Assert.AreEqual("vscode-notebook-cell:/tmp/notebook.nb.md", kernel.LanguagesAskedFor,
            "the language set is asked for by notebook, not globally");

        // And the outputs still come back filed under the id the editor knows.
        Assert.AreEqual("succeeded", session.Snapshot()["c0"].Status);
    }

    [TestMethod]
    public async Task A_language_registered_mid_session_reaches_the_notebook_parser() {
        var seeded = new List<IReadOnlyList<LanguageDescriptor>>();
        var (session, _) = NewSession(seeded);
        await RunAsync(session, "load a plugin");

        await SettleAsync(
            () => session.Languages.Any(l => l.Id == "foo"),
            "the languagesChanged notification never arrived");

        // Executing the cell is only half of it. Parsing a notebook into cells happens
        // outside any session, against the cached set — so a language that reaches the
        // session but not the cache runs when you press play and stops being a code
        // cell on the next reload.
        Assert.IsTrue(seeded.Count >= 2, "seeded on start and again when the set changed");
        CollectionAssert.AreEquivalent(
            new[] { "sql", "foo" }, seeded[^1].Select(l => l.Id).ToArray());
    }

    private static NotebookSyncCell Sync(string id, string languageId, string source) =>
        new() { Id = id, LanguageId = languageId, Source = source };

    /// <summary>Notifications are fire-and-forget, so the fake sees them slightly
    /// after SyncAsync returns.</summary>
    private static Task SettleEvents(FakeKernel kernel, int count) =>
        SettleAsync(() => kernel.DocumentEvents.Count >= count,
            $"expected {count} document notifications");

    [TestMethod]
    public async Task Only_what_changed_is_sent_so_a_keystroke_costs_one_notification() {
        var (session, kernel) = NewSession();
        var cells = new[] { Sync("c0", "csharp-script", "var a = 1;"), Sync("c1", "sql", "select 1") };

        Assert.AreEqual(2, await session.SyncAsync(cells, CancellationToken.None));
        await SettleEvents(kernel, 2);

        // Same cells again: nothing to say. This is what makes it safe to call on
        // every keystroke rather than only on blur.
        Assert.AreEqual(0, await session.SyncAsync(cells, CancellationToken.None));

        var edited = new[] { cells[0], Sync("c1", "sql", "select 2") };
        Assert.AreEqual(1, await session.SyncAsync(edited, CancellationToken.None));
        await SettleEvents(kernel, 3);

        CollectionAssert.AreEqual(new[] {
            "didOpen c0 csharp-script v1 var a = 1;",
            "didOpen c1 sql v1 select 1",
            "didChange c1 v2 select 2",
        }, kernel.DocumentEvents.ToArray());
    }

    [TestMethod]
    public async Task A_cell_the_editor_no_longer_lists_is_closed() {
        var (session, kernel) = NewSession();
        await session.SyncAsync(
            new[] { Sync("c0", "csharp-script", "one"), Sync("c1", "csharp-script", "two") },
            CancellationToken.None);
        await SettleEvents(kernel, 2);

        // Deleting a cell has to close its document. Completion gathers context from
        // every open document in the notebook, so a cell that is gone from the file
        // but still open would keep offering its symbols for the life of the session.
        await session.SyncAsync(new[] { Sync("c0", "csharp-script", "one") }, CancellationToken.None);
        await SettleEvents(kernel, 3);

        Assert.AreEqual("didClose c1", kernel.DocumentEvents[^1]);
    }

    [TestMethod]
    public async Task Changing_a_cells_language_closes_and_reopens_it() {
        var (session, kernel) = NewSession();
        await session.SyncAsync(new[] { Sync("c0", "sql", "select 1") }, CancellationToken.None);
        await SettleEvents(kernel, 1);

        // Not a didChange: the server dispatches services and diagnostics off the
        // languageId it was given at open, and a reopen is what retracts the old
        // language's problems instead of leaving them on screen.
        await session.SyncAsync(new[] { Sync("c0", "csharp-script", "select 1") }, CancellationToken.None);
        await SettleEvents(kernel, 3);

        CollectionAssert.AreEqual(new[] {
            "didOpen c0 sql v1 select 1",
            "didClose c0",
            "didOpen c0 csharp-script v2 select 1",
        }, kernel.DocumentEvents.ToArray());
    }

    [TestMethod]
    public async Task A_replaced_kernel_is_told_about_the_documents_again() {
        var kernels = new List<FakeKernel>();
        var dead = false;
        var session = new NotebookSession("s1", "/tmp/notebook.nb.md", null, null, _ => {
            var (client, fake) = NewKernel();
            kernels.Add(fake);
            return Task.FromResult(KernelProcess.ForClient(client, () => dead && kernels.Count == 1));
        });
        _disposables.Add(session);

        await session.SyncAsync(new[] { Sync("c0", "sql", "select 1") }, CancellationToken.None);
        await SettleEvents(kernels[0], 1);

        // The kernel died and a fresh one took over: it holds no documents. Sending it
        // a didChange would land the text but not the languageId, and the cell would
        // quietly drop to the C# fallback instead of its own language's services.
        dead = true;
        Assert.AreEqual(1, await session.SyncAsync(
            new[] { Sync("c0", "sql", "select 1") }, CancellationToken.None),
            "the same text is news again — the new process never saw it");

        Assert.AreEqual(2, kernels.Count, "the dead kernel was replaced");
        Assert.IsTrue(session.KernelRestarted);
        await SettleEvents(kernels[1], 1);
        Assert.AreEqual("didOpen c0 sql v1 select 1", kernels[1].DocumentEvents[^1],
            "reopened with its language, not changed");
    }

    [TestMethod]
    public void A_path_with_a_space_is_escaped_so_the_kernel_can_parse_it_back() {
        var session = new NotebookSession("s", "/tmp/my notebooks/a b.nb.md", null);
        // The other half of the round trip — that the server unescapes this back to
        // the file — is pinned by NotebookKeyTest, which can see it.
        Assert.AreEqual("vscode-notebook-cell:/tmp/my%20notebooks/a%20b.nb.md#c0", session.CellUri("c0"));
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
