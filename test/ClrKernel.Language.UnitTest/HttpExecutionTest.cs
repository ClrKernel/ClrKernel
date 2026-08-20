using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// End-to-end HTTP-cell tests against a throwaway in-process HttpListener:
/// executor round-trips, request chaining across a cell, the engine's #!http
/// dispatch, and notebook (.nb.md / .dib) http-cell extraction.
/// </summary>
[TestClass]
public class HttpExecutionTest {
    private static HttpListener _listener;
    private static string _base;
    private static Thread _serverThread;
    private static volatile bool _running;

    [ClassInitialize]
    public static void StartServer(TestContext _) {
        var port = FreePort();
        _base = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _running = true;
        _serverThread = new Thread(Serve) { IsBackground = true };
        _serverThread.Start();
    }

    [ClassCleanup]
    public static void StopServer() {
        _running = false;
        try { _listener.Stop(); } catch { /* ignore */ }
    }

    private static int FreePort() {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void Serve() {
        while (_running) {
            HttpListenerContext ctx;
            try {
                ctx = _listener.GetContext();
            } catch {
                return;
            }
            try {
                Route(ctx);
            } catch {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
            }
        }
    }

    private static void Route(HttpListenerContext ctx) {
        var path = ctx.Request.Url.AbsolutePath;
        var response = ctx.Response;

        if (path == "/json") {
            Write(response, 200, "application/json", "{\"message\":\"hi\",\"token\":\"tok-42\"}");
        } else if (path == "/login") {
            Write(response, 200, "application/json", "{\"token\":\"abc123\"}");
        } else if (path == "/secure") {
            var auth = ctx.Request.Headers["Authorization"];
            if (auth == "Bearer abc123") {
                Write(response, 200, "application/json", "{\"ok\":true}");
            } else {
                Write(response, 401, "application/json", "{\"ok\":false}");
            }
        } else {
            Write(response, 404, "text/plain", "not found");
        }
    }

    private static void Write(HttpListenerResponse response, int status, string contentType, string body) {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    // --- executor ----------------------------------------------------------

    [TestMethod]
    public async Task Executor_sends_request_and_captures_response() {
        var spec = HttpFileParser.Parse("GET " + _base + "json\n").Requests.Single();
        var resolver = new HttpVariableResolver(null, new Dictionary<string, HttpExchange>());
        var executor = new HttpRequestExecutor(Directory.GetCurrentDirectory());

        var exchange = await executor.SendAsync(spec, resolver);

        Assert.IsFalse(exchange.IsError, exchange.Error);
        Assert.AreEqual(200, exchange.StatusCode);
        StringAssert.Contains(exchange.ContentType, "application/json");
        StringAssert.Contains(exchange.BodyText, "tok-42");
        Assert.IsTrue(exchange.ElapsedMs >= 0);
    }

    // --- chaining across a cell -------------------------------------------

    [TestMethod]
    public async Task Session_chains_token_from_one_request_to_the_next() {
        var emitted = new List<DisplayData>();
        void OnCell(DisplayCell cell) => emitted.Add(MimeBundler.Bundle(cell));
        DisplayValues.OnCellDisplayed += OnCell;
        try {
            var session = new HttpSession(Directory.GetCurrentDirectory());
            var cell =
                "# @name login\n" +
                "POST " + _base + "login\n" +
                "###\n" +
                "GET " + _base + "secure\n" +
                "Authorization: Bearer {{login.response.body.$.token}}\n";

            var last = await session.ExecuteAsync(cell);

            Assert.AreEqual(2, emitted.Count, "expected one response card per request");
            Assert.AreEqual(200, last.StatusCode, "chained auth should succeed");
            StringAssert.Contains(last.BodyText, "\"ok\":true");
        } finally {
            DisplayValues.OnCellDisplayed -= OnCell;
        }
    }

    // --- engine #!http dispatch -------------------------------------------

    [TestMethod]
    public async Task Engine_routes_http_selector_cell_to_http_session() {
        var emitted = new List<DisplayData>();
        void OnCell(DisplayCell cell) => emitted.Add(MimeBundler.Bundle(cell));
        DisplayValues.OnCellDisplayed += OnCell;
        try {
            var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
            var result = await engine.ExecuteAsync("#!http\nGET " + _base + "json\n");

            Assert.IsNull(result, "http cells emit display data and return null");
            Assert.AreEqual(1, emitted.Count);
            StringAssert.Contains((string)emitted[0].Data["text/html"], "ck-2xx");
        } finally {
            DisplayValues.OnCellDisplayed -= OnCell;
        }
    }

    // --- notebook extraction ----------------------------------------------

    [TestMethod]
    public void Markdown_http_tag_becomes_http_block() {
        var md =
            "# Title\n\n" +
            "```http\n" +
            "GET https://example.com/x\n" +
            "```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!http\n");
        StringAssert.Contains(blocks[0], "GET https://example.com/x");
    }

    [TestMethod]
    public void Dib_http_section_becomes_http_block() {
        var dib =
            "#!csharp\n" +
            "var x = 1;\n" +
            "#!http\n" +
            "GET https://example.com/y\n";
        var blocks = NotebookImporter.ParseDib(dib);
        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("var x = 1;", blocks[0]);
        StringAssert.StartsWith(blocks[1], "#!http\n");
        StringAssert.Contains(blocks[1], "GET https://example.com/y");
    }
}
