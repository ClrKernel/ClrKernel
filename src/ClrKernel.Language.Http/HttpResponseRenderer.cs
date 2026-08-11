using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ClrKernel.Language.Http;

/// <summary>
/// Renders an <see cref="HttpExchange"/> as a self-contained, theme-aware HTML
/// card: a color-coded status badge, timing and size, collapsible request and
/// response headers, and a content-type-aware body (pretty-printed and
/// highlighted JSON, inline images, escaped source otherwise). No external
/// assets; colors come from VS Code theme variables with fallbacks so it reads
/// well in light and dark, in a notebook or a plain browser.
/// </summary>
public static class HttpResponseRenderer {
    public const int BodyPreviewLimit = 200_000;

    /// <summary>Builds the display bundle (text/html card + a text/plain fallback).</summary>
    public static (string Html, string Text) Render(HttpExchange exchange) {
        return exchange.IsError
            ? (RenderError(exchange), RenderErrorText(exchange))
            : (RenderCard(exchange), RenderText(exchange));
    }

    // --- HTML --------------------------------------------------------------

    private static string RenderCard(HttpExchange exchange) {
        var id = "clrkernel-http-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("<div class=\"clrkernel-http\" id=\"").Append(id).Append("\">");
        sb.Append(Style(id));

        // Status row.
        sb.Append("<div class=\"ck-http-status\">");
        sb.Append("<span class=\"ck-http-method\">").Append(Encode(exchange.RequestMethod)).Append("</span>");
        sb.Append("<span class=\"ck-http-badge ").Append(StatusClass(exchange.StatusCode)).Append("\">")
            .Append(exchange.StatusCode).Append(' ').Append(Encode(exchange.ReasonPhrase)).Append("</span>");
        sb.Append("<span class=\"ck-http-meta\">")
            .Append(FormatElapsed(exchange.ElapsedMs)).Append(" · ").Append(FormatSize(exchange.ContentLength))
            .Append("</span>");
        sb.Append("</div>");

        // URL.
        sb.Append("<div class=\"ck-http-url\">").Append(Encode(exchange.RequestUrl)).Append("</div>");

        // Headers (collapsible).
        if (exchange.RequestHeaders.Count > 0 || exchange.RequestBody != null) {
            sb.Append("<details class=\"ck-http-details\"><summary>Request</summary>");
            AppendHeaderTable(sb, exchange.RequestHeaders);
            if (exchange.RequestBody != null) {
                sb.Append("<pre class=\"ck-http-reqbody\">").Append(Encode(Truncate(exchange.RequestBody))).Append("</pre>");
            }
            sb.Append("</details>");
        }
        sb.Append("<details class=\"ck-http-details\"><summary>Response headers</summary>");
        AppendHeaderTable(sb, exchange.ResponseHeaders);
        sb.Append("</details>");

        // Body.
        sb.Append("<div class=\"ck-http-body\">").Append(RenderBody(exchange)).Append("</div>");

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RenderError(HttpExchange exchange) {
        var id = "clrkernel-http-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("<div class=\"clrkernel-http\" id=\"").Append(id).Append("\">");
        sb.Append(Style(id));
        sb.Append("<div class=\"ck-http-status\">");
        sb.Append("<span class=\"ck-http-method\">").Append(Encode(exchange.RequestMethod)).Append("</span>");
        sb.Append("<span class=\"ck-http-badge ck-5xx\">Request failed</span>");
        sb.Append("</div>");
        sb.Append("<div class=\"ck-http-url\">").Append(Encode(exchange.RequestUrl)).Append("</div>");
        sb.Append("<pre class=\"ck-http-error\">").Append(Encode(exchange.Error)).Append("</pre>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendHeaderTable(StringBuilder sb, System.Collections.Generic.List<HttpNameValue> headers) {
        if (headers.Count == 0) {
            sb.Append("<div class=\"ck-http-empty\">(none)</div>");
            return;
        }
        sb.Append("<table class=\"ck-http-headers\">");
        foreach (var header in headers) {
            sb.Append("<tr><td class=\"ck-http-hname\">").Append(Encode(header.Name))
                .Append("</td><td class=\"ck-http-hval\">").Append(Encode(header.Value)).Append("</td></tr>");
        }
        sb.Append("</table>");
    }

    private static string RenderBody(HttpExchange exchange) {
        var contentType = (exchange.ContentType ?? string.Empty).ToLowerInvariant();

        // Images render inline.
        if (contentType.StartsWith("image/", StringComparison.Ordinal) && exchange.BodyBytes != null) {
            var mime = contentType.Split(';')[0];
            var data = Convert.ToBase64String(exchange.BodyBytes);
            return "<img class=\"ck-http-img\" alt=\"response image\" src=\"data:" + mime + ";base64," + data + "\" />";
        }

        var body = exchange.BodyText;
        if (string.IsNullOrEmpty(body)) {
            if (exchange.BodyBytes != null && exchange.BodyBytes.Length > 0) {
                return "<div class=\"ck-http-empty\">" + FormatSize(exchange.ContentLength) + " (binary)</div>";
            }
            return "<div class=\"ck-http-empty\">(empty body)</div>";
        }

        var truncated = body.Length > BodyPreviewLimit;
        var shown = truncated ? body.Substring(0, BodyPreviewLimit) : body;

        string inner;
        if (contentType.Contains("json") || LooksLikeJson(shown)) {
            inner = TryHighlightJson(shown, out var highlighted)
                ? "<pre class=\"ck-http-code ck-http-json\">" + highlighted + "</pre>"
                : "<pre class=\"ck-http-code\">" + Encode(shown) + "</pre>";
        } else {
            inner = "<pre class=\"ck-http-code\">" + Encode(shown) + "</pre>";
        }

        if (truncated) {
            inner += "<div class=\"ck-http-empty\">(body truncated at " + FormatSize(BodyPreviewLimit) + ")</div>";
        }
        return inner;
    }

    // --- JSON highlighting -------------------------------------------------

    private static bool LooksLikeJson(string text) {
        var t = text.TrimStart();
        return t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal);
    }

    private static bool TryHighlightJson(string json, out string html) {
        html = null;
        try {
            using var document = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            WriteElement(document.RootElement, sb, 0);
            html = sb.ToString();
            return true;
        } catch (JsonException) {
            return false;
        }
    }

    private static void WriteElement(JsonElement element, StringBuilder sb, int depth) {
        switch (element.ValueKind) {
            case JsonValueKind.Object: {
                    sb.Append('{');
                    var first = true;
                    foreach (var property in element.EnumerateObject()) {
                        if (!first) {
                            sb.Append(',');
                        }
                        first = false;
                        sb.Append('\n').Append(Indent(depth + 1));
                        sb.Append("<span class=\"ck-json-key\">\"").Append(Encode(property.Name)).Append("\"</span>: ");
                        WriteElement(property.Value, sb, depth + 1);
                    }
                    if (!first) {
                        sb.Append('\n').Append(Indent(depth));
                    }
                    sb.Append('}');
                    break;
                }
            case JsonValueKind.Array: {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in element.EnumerateArray()) {
                        if (!first) {
                            sb.Append(',');
                        }
                        first = false;
                        sb.Append('\n').Append(Indent(depth + 1));
                        WriteElement(item, sb, depth + 1);
                    }
                    if (!first) {
                        sb.Append('\n').Append(Indent(depth));
                    }
                    sb.Append(']');
                    break;
                }
            case JsonValueKind.String:
                sb.Append("<span class=\"ck-json-str\">\"").Append(Encode(element.GetString())).Append("\"</span>");
                break;
            case JsonValueKind.Number:
                sb.Append("<span class=\"ck-json-num\">").Append(Encode(element.GetRawText())).Append("</span>");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.Append("<span class=\"ck-json-bool\">").Append(element.GetRawText()).Append("</span>");
                break;
            case JsonValueKind.Null:
                sb.Append("<span class=\"ck-json-null\">null</span>");
                break;
        }
    }

    private static string Indent(int depth) => new string(' ', depth * 2);

    // --- text/plain fallback ----------------------------------------------

    private static string RenderText(HttpExchange exchange) {
        var sb = new StringBuilder();
        sb.Append(exchange.RequestMethod).Append(' ').Append(exchange.RequestUrl).Append('\n');
        sb.Append(exchange.StatusCode).Append(' ').Append(exchange.ReasonPhrase)
            .Append("  (").Append(FormatElapsed(exchange.ElapsedMs)).Append(", ").Append(FormatSize(exchange.ContentLength)).Append(")\n");
        if (!string.IsNullOrEmpty(exchange.BodyText)) {
            sb.Append('\n').Append(Truncate(exchange.BodyText));
        }
        return sb.ToString();
    }

    private static string RenderErrorText(HttpExchange exchange) =>
        exchange.RequestMethod + " " + exchange.RequestUrl + "\nRequest failed: " + exchange.Error;

    // --- helpers -----------------------------------------------------------

    private static string StatusClass(int status) {
        if (status >= 200 && status < 300) {
            return "ck-2xx";
        }
        if (status >= 300 && status < 400) {
            return "ck-3xx";
        }
        if (status >= 400 && status < 500) {
            return "ck-4xx";
        }
        return "ck-5xx";
    }

    private static string FormatElapsed(double ms) =>
        ms >= 1000
            ? (ms / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s"
            : ms.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string FormatSize(long bytes) {
        if (bytes < 1024) {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024) {
            return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }
        return (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    private static string Truncate(string text) =>
        text.Length > BodyPreviewLimit ? text.Substring(0, BodyPreviewLimit) + "\n…(truncated)" : text;

    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Style(string id) {
        var s = "#" + id;
        return "<style>" +
            s + "{font-family:var(--vscode-font-family,-apple-system,Segoe UI,sans-serif);font-size:var(--vscode-font-size,13px);color:var(--vscode-foreground,#1f1f1f);border:1px solid var(--vscode-panel-border,rgba(128,128,128,.35));border-radius:5px;overflow:hidden;max-width:100%}" +
            s + " .ck-http-status{display:flex;align-items:center;gap:8px;padding:7px 10px;background:var(--vscode-editorWidget-background,rgba(128,128,128,.08));border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.25))}" +
            s + " .ck-http-method{font-weight:700;font-family:var(--vscode-editor-font-family,monospace);font-size:11px;letter-spacing:.04em;opacity:.85}" +
            s + " .ck-http-badge{font-weight:600;padding:1px 8px;border-radius:10px;color:#fff;font-size:12px}" +
            s + " .ck-2xx{background:#2ea043}" + s + " .ck-3xx{background:#1f6feb}" +
            s + " .ck-4xx{background:#d29922}" + s + " .ck-5xx{background:#cf222e}" +
            s + " .ck-http-meta{margin-left:auto;opacity:.7;font-size:11px;white-space:nowrap}" +
            s + " .ck-http-url{padding:5px 10px;font-family:var(--vscode-editor-font-family,monospace);font-size:11px;word-break:break-all;opacity:.85;border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.15))}" +
            s + " .ck-http-details{border-bottom:1px solid var(--vscode-panel-border,rgba(128,128,128,.15))}" +
            s + " .ck-http-details>summary{cursor:pointer;padding:5px 10px;font-size:11px;opacity:.8;user-select:none}" +
            s + " .ck-http-headers{border-collapse:collapse;margin:0 10px 8px;font-family:var(--vscode-editor-font-family,monospace);font-size:11px}" +
            s + " .ck-http-headers td{padding:1px 10px 1px 0;vertical-align:top}" +
            s + " .ck-http-hname{color:var(--vscode-descriptionForeground,#888);white-space:nowrap}" +
            s + " .ck-http-hval{word-break:break-all}" +
            s + " .ck-http-empty{padding:4px 10px 8px;opacity:.6;font-size:11px;font-style:italic}" +
            s + " .ck-http-body{padding:0}" +
            s + " .ck-http-code,.ck-http-reqbody,.ck-http-error{margin:0;padding:10px;overflow:auto;max-height:460px;font-family:var(--vscode-editor-font-family,monospace);font-size:12px;line-height:1.45;white-space:pre;tab-size:2}" +
            s + " .ck-http-reqbody{max-height:200px;background:var(--vscode-textCodeBlock-background,rgba(128,128,128,.08))}" +
            s + " .ck-http-error{color:var(--vscode-errorForeground,#cf222e)}" +
            s + " .ck-http-img{max-width:100%;display:block;padding:10px}" +
            s + " .ck-json-key{color:var(--vscode-symbolIcon-propertyForeground,#0451a5)}" +
            s + " .ck-json-str{color:var(--vscode-debugTokenExpression-string,#a31515)}" +
            s + " .ck-json-num{color:var(--vscode-debugTokenExpression-number,#098658)}" +
            s + " .ck-json-bool{color:var(--vscode-debugTokenExpression-boolean,#0000ff)}" +
            s + " .ck-json-null{color:var(--vscode-debugTokenExpression-boolean,#0000ff);opacity:.7}" +
            "</style>";
    }
}
