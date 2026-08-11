using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ClrKernel.Core.ExtensionServer.Lsp;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ClrKernel.Core.ExtensionServer;

/// <summary>
/// Hosts the unified ClrKernel <see cref="LspServer"/> over stdio using
/// Language Server Protocol framing (Content-Length headers) with a camelCase
/// System.Text.Json formatter. Language features and cell execution share one
/// connection and one engine. Logs go to stderr so they never corrupt the
/// protocol stream.
/// </summary>
public static class LspHost {
    public static async Task RunAsync() {
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        Console.SetOut(TextWriter.Null);

        using var loggerFactory = LoggerFactory.Create(builder => {
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("ClrKernel.Lsp");

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        var server = new LspServer(loggerFactory);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stdout, stdin, formatter), server);
        server.Rpc = rpc;
        rpc.StartListening();

        logger.LogInformation("ClrKernel LSP listening on stdio");
        await rpc.Completion.ConfigureAwait(false);
        logger.LogInformation("connection closed, exiting");
    }
}
