using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.ExtensionServer.Lsp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace ClrKernel.Language.UnitTest;

/// <summary>
/// Published diagnostics must be retracted, not just published. A cell that
/// changes language — SQL to C#, say — closes and reopens under the new
/// languageId, and without an explicit clear the old language's problems stay in
/// the Problems panel until the kernel restarts.
/// </summary>
[TestClass]
public class LspDiagnosticsTest {
    /// <summary>Collects the textDocument/publishDiagnostics notifications the server sends.</summary>
    private sealed class DiagnosticsSink {
        public List<(string Uri, int Count)> Published { get; } = new();

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void Publish(PublishDiagnosticsParams p) {
            lock (Published) {
                Published.Add((p.Uri, p.Diagnostics?.Count ?? 0));
            }
        }
    }

    private const string _cellUri = "vscode-notebook-cell:/tmp/diag.nb.md#c1";

    private static (LspServer Server, DiagnosticsSink Sink, JsonRpc Client, JsonRpc Host) Connect() {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new LspServer(NullLoggerFactory.Instance);
        var host = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream), server);
        server.Rpc = host;
        host.StartListening();

        var sink = new DiagnosticsSink();
        var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream), sink);
        client.StartListening();
        return (server, sink, client, host);
    }

    private static Task Open(JsonRpc client, string languageId, string text) =>
        client.NotifyWithParameterObjectAsync("textDocument/didOpen", new {
            textDocument = new { uri = _cellUri, languageId, version = 1, text },
        });

    /// <summary>Notifications are fire-and-forget; give the server a moment to answer.</summary>
    private static async Task SettleAsync(DiagnosticsSink sink, int expected) {
        for (var i = 0; i < 100 && sink.Published.Count < expected; i++) {
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task Changing_a_cell_from_sql_to_csharp_clears_the_sql_problems() {
        var (_, sink, client, host) = Connect();
        using (client)
        using (host) {
            // A SQL cell with C# pasted into it: genuinely invalid T-SQL.
            await Open(client, "sql", "var x = 10;");
            await SettleAsync(sink, 1);
            Assert.IsTrue(sink.Published.Count >= 1 && sink.Published[0].Count > 0,
                "invalid T-SQL should report problems");

            // The user switches the cell to C#. VS Code closes the document and
            // reopens it under the new languageId; either step must clear.
            await client.NotifyWithParameterObjectAsync("textDocument/didClose",
                new { textDocument = new { uri = _cellUri } });
            await SettleAsync(sink, 2);
            await Open(client, "csharp-script", "var x = 10;");
            await SettleAsync(sink, 2);

            Assert.AreEqual(_cellUri, sink.Published[^1].Uri);
            Assert.AreEqual(0, sink.Published[^1].Count,
                "the SQL problems must be retracted, not left on screen until a kernel restart");
        }
    }

    [TestMethod]
    public async Task Fixing_the_sql_clears_the_problems_without_a_language_change() {
        var (_, sink, client, host) = Connect();
        using (client)
        using (host) {
            await Open(client, "sql", "selct * frm t");
            await SettleAsync(sink, 1);
            Assert.IsTrue(sink.Published[0].Count > 0);

            await client.NotifyWithParameterObjectAsync("textDocument/didChange", new {
                textDocument = new { uri = _cellUri },
                contentChanges = new[] { new { text = "select 1" } },
            });
            await SettleAsync(sink, 2);
            Assert.AreEqual(0, sink.Published[^1].Count, "valid SQL retracts the earlier problems");
        }
    }

    [TestMethod]
    public async Task An_undiagnosed_cell_stays_quiet() {
        var (_, sink, client, host) = Connect();
        using (client)
        using (host) {
            // C# has no cell-language diagnostics; with nothing on screen there is
            // nothing to retract, so the server must not chatter per keystroke.
            await Open(client, "csharp-script", "var x = 1;");
            await Task.Delay(150);
            Assert.AreEqual(0, sink.Published.Count);
        }
    }
}
