using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Jupyter.Kernels;
using ClrKernel.Jupyter.Protocols;
using ClrKernel.LanguageServices;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jupyter.RequestHandlers;

/// <summary>
/// Answers Jupyter complete_request (tab completion) and inspect_request
/// (introspection / hover) using the shared <see cref="ScriptLanguageService"/>
/// over the running session's state (<see cref="InteractiveScriptEngine.Current"/>),
/// so completions and inspection reflect executed cells, #r "nuget:" types, and
/// imports — exactly what the VS Code LSP path serves.
/// </summary>
public class LanguageRequestHandler {
    private readonly MessageSender _shell;
    private readonly ScriptLanguageService _language = new();
    private readonly ILogger _logger;

    public LanguageRequestHandler(MessageSender shell, ILoggerFactory loggerFactory) {
        _shell = shell;
        _logger = loggerFactory.CreateLogger(nameof(LanguageRequestHandler));
    }

    public async Task ProcessCompleteAsync(Message<CompleteRequest> message) {
        var code = message.Content?.Code ?? string.Empty;
        var cursor = Clamp(message.Content?.CursorPos ?? 0, code.Length);
        var reply = new CompleteReply { CursorStart = cursor, CursorEnd = cursor };

        try {
            var snapshot = InteractiveScriptEngine.Current?.SnapshotState();
            if (snapshot != null) {
                var result = await _language.GetCompletionsAsync(snapshot, code, cursor).ConfigureAwait(false);
                reply.Matches = result.Items.Select(i => i.Label).Distinct().ToList();
                reply.CursorStart = result.ReplaceStart;
                reply.CursorEnd = result.ReplaceStart + result.ReplaceLength;
            }
        } catch (Exception e) {
            _logger.LogError(e, "complete_request failed");
        }

        _shell.Send(message, reply, MessageType.CompleteReply);
    }

    public async Task ProcessInspectAsync(Message<InspectRequest> message) {
        var code = message.Content?.Code ?? string.Empty;
        var cursor = Clamp(message.Content?.CursorPos ?? 0, code.Length);
        var reply = new InspectReply { Found = false };

        try {
            var snapshot = InteractiveScriptEngine.Current?.SnapshotState();
            if (snapshot != null) {
                var hover = await _language.GetHoverAsync(snapshot, code, cursor).ConfigureAwait(false);
                if (hover != null && !string.IsNullOrEmpty(hover.Markdown)) {
                    reply.Found = true;
                    reply.Data = new Dictionary<string, object> { ["text/plain"] = hover.Markdown };
                }
            }
        } catch (Exception e) {
            _logger.LogError(e, "inspect_request failed");
        }

        _shell.Send(message, reply, MessageType.InspectReply);
    }

    private static int Clamp(int value, int max) => Math.Max(0, Math.Min(value, max));
}
