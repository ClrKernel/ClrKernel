using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Language.Http;

/// <summary>
/// Runs <c>#!http</c> cells for a notebook session. Parses a cell's <c>.http</c>
/// content, resolves variables (accumulated across cells, like one growing
/// <c>.http</c> file), sends each request, and emits a rich response card per
/// request. Named responses are remembered so later requests can chain off them
/// (<c>{{login.response.body.$.token}}</c>).
/// </summary>
public sealed class HttpSession {
    private readonly List<HttpVariableDefinition> _variables = new();
    private readonly Dictionary<string, HttpExchange> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpRequestExecutor _executor;

    public HttpSession(string baseDirectory) {
        _executor = new HttpRequestExecutor(baseDirectory);
    }

    /// <summary>Named responses captured so far (for reference/testing).</summary>
    public IReadOnlyDictionary<string, HttpExchange> Responses => _responses;

    /// <summary>
    /// Executes one <c>#!http</c> cell body. Each request's response card is
    /// published through <see cref="DisplayDataEmitter"/> as it completes.
    /// Returns the last <see cref="HttpExchange"/> (null for an empty cell).
    /// </summary>
    public async Task<HttpExchange> ExecuteAsync(string cellBody, CancellationToken cancellationToken = default) {
        var file = HttpFileParser.Parse(cellBody);

        // Cell-scoped variables accumulate into the session (a later cell sees an
        // earlier cell's @definitions), matching a single growing .http file.
        _variables.AddRange(file.Variables);

        HttpExchange last = null;
        foreach (var request in file.Requests) {
            var resolver = new HttpVariableResolver(_variables, _responses);
            var exchange = await _executor.SendAsync(request, resolver, cancellationToken);

            if (!string.IsNullOrEmpty(exchange.Name)) {
                _responses[exchange.Name] = exchange;
            }

            var (html, text) = HttpResponseRenderer.Render(exchange);
            DisplayDataEmitter.Emit(new DisplayData(text, html));
            last = exchange;
        }

        return last;
    }
}
