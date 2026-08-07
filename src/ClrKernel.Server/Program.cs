using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ClrKernel.Server;

/// <summary>
/// stdio JSON-RPC notebook host. stdin/stdout carry Content-Length framed
/// JSON-RPC (compatible with vscode-jsonrpc's StreamMessageReader/Writer);
/// logs go to stderr so they can never corrupt the protocol stream.
/// </summary>
public static class Program {
    public static async Task Main(string[] args) {
        // Claim the real stdio streams for the RPC channel FIRST, then detach
        // Console so stray writes (before a cell's ConsoleProxy takes over)
        // cannot corrupt message framing.
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        Console.SetOut(TextWriter.Null);

        using var loggerFactory = LoggerFactory.Create(builder => {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("ClrKernel.Server");

        var server = new NotebookServer(loggerFactory);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stdout, stdin), server);
        server.Rpc = rpc;
        rpc.StartListening();

        logger.LogInformation("ClrKernel.Server listening on stdio");
        await rpc.Completion.ConfigureAwait(false);
        logger.LogInformation("connection closed, exiting");
    }
}
