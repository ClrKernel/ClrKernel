using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClrKernel.Http;

/// <summary>An ordered file-scoped variable definition (<c>@name = value</c>).</summary>
public sealed class HttpVariableDefinition {
    public HttpVariableDefinition(string name, string value) {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    /// <summary>Raw value — may itself reference <c>{{other}}</c> variables or system variables.</summary>
    public string Value { get; }
}

/// <summary>The parse of a <c>.http</c> document: its variable definitions and requests, in order.</summary>
public sealed class HttpFile {
    public List<HttpVariableDefinition> Variables { get; } = new();
    public List<HttpRequestSpec> Requests { get; } = new();
}

/// <summary>
/// Parses <c>.http</c> / <c>.rest</c> documents in the VS Code REST Client
/// syntax: <c>###</c>-separated requests, <c>@name = value</c> variables,
/// <c>// @name</c> request names, headers, and bodies (inline or <c>&lt; file</c>).
/// Values keep their <c>{{…}}</c> placeholders; resolution happens at send time.
/// </summary>
public static class HttpFileParser {
    private static readonly HashSet<string> _methods = new(StringComparer.OrdinalIgnoreCase) {
        "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT",
    };

    private static readonly Regex _variablePattern =
        new(@"^\s*@(?<name>[A-Za-z_][\w\-]*)\s*=\s*(?<value>.*)$", RegexOptions.Compiled);

    // "# @name foo" / "// @name foo" / "// @name = foo"
    private static readonly Regex _namePattern =
        new(@"^\s*(?:#|//)\s*@name\s*=?\s*(?<name>\S+)\s*$", RegexOptions.Compiled);

    private static readonly Regex _separatorPattern =
        new(@"^###(?<label>.*)$", RegexOptions.Compiled);

    private static readonly Regex _versionPattern =
        new(@"^HTTP/\d(?:\.\d)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HttpFile Parse(string text) {
        var file = new HttpFile();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        HttpRequestSpec current = null;
        string pendingName = null;
        string pendingLabel = null;
        var mode = Mode.PreRequest;
        var body = new StringBuilder();
        var headerBlankSeen = false;

        void Finalize() {
            if (current != null) {
                current.Body = NormalizeBody(body.ToString(), current);
                file.Requests.Add(current);
            }
            current = null;
            body.Clear();
            headerBlankSeen = false;
            mode = Mode.PreRequest;
        }

        foreach (var rawLine in lines) {
            var line = rawLine;

            var separator = _separatorPattern.Match(line);
            if (separator.Success) {
                Finalize();
                var label = separator.Groups["label"].Value.Trim();
                pendingLabel = label.Length > 0 ? label.TrimStart('#').Trim() : null;
                pendingName = null;
                continue;
            }

            switch (mode) {
                case Mode.PreRequest: {
                        if (line.Trim().Length == 0) {
                            continue; // skip blank lines before the request line
                        }
                        var variable = _variablePattern.Match(line);
                        if (variable.Success) {
                            file.Variables.Add(new HttpVariableDefinition(
                                variable.Groups["name"].Value, variable.Groups["value"].Value.Trim()));
                            continue;
                        }
                        if (IsComment(line)) {
                            CaptureName(line, ref pendingName);
                            continue;
                        }
                        // First non-comment, non-variable line is the request line.
                        current = ParseRequestLine(line);
                        current.Name = pendingName;
                        current.Label = pendingLabel;
                        pendingName = null;
                        pendingLabel = null;
                        mode = Mode.Headers;
                        break;
                    }

                case Mode.Headers: {
                        if (line.Trim().Length == 0) {
                            headerBlankSeen = true;
                            mode = Mode.Body;
                            continue;
                        }
                        if (IsComment(line)) {
                            CaptureName(line, ref pendingName);
                            if (pendingName != null && current.Name == null) {
                                current.Name = pendingName;
                                pendingName = null;
                            }
                            continue;
                        }
                        var trimmedStart = line.TrimStart();
                        if ((trimmedStart.StartsWith("?", StringComparison.Ordinal)
                             || trimmedStart.StartsWith("&", StringComparison.Ordinal))
                            && char.IsWhiteSpace(line, 0)) {
                            // Query-string continuation of the request line.
                            current.Url += trimmedStart;
                            continue;
                        }
                        var colon = line.IndexOf(':');
                        if (colon > 0) {
                            var name = line.Substring(0, colon).Trim();
                            var value = line.Substring(colon + 1).Trim();
                            current.Headers.Add(new HttpHeaderLine(name, value));
                        }
                        break;
                    }

                case Mode.Body: {
                        body.Append(rawLine).Append('\n');
                        break;
                    }
            }
        }

        Finalize();
        _ = headerBlankSeen;
        return file;
    }

    private enum Mode { PreRequest, Headers, Body }

    private static bool IsComment(string line) {
        var t = line.TrimStart();
        return (t.StartsWith("#", StringComparison.Ordinal) && !t.StartsWith("###", StringComparison.Ordinal))
            || t.StartsWith("//", StringComparison.Ordinal);
    }

    private static void CaptureName(string line, ref string pendingName) {
        var m = _namePattern.Match(line);
        if (m.Success) {
            pendingName = m.Groups["name"].Value;
        }
    }

    private static HttpRequestSpec ParseRequestLine(string line) {
        var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var spec = new HttpRequestSpec();
        if (parts.Length == 0) {
            return spec;
        }

        var startIndex = 0;
        if (_methods.Contains(parts[0])) {
            spec.Method = parts[0].ToUpperInvariant();
            startIndex = 1;
        }

        var endIndex = parts.Length;
        if (parts.Length - startIndex >= 2 && _versionPattern.IsMatch(parts[parts.Length - 1])) {
            spec.Version = parts[parts.Length - 1];
            endIndex = parts.Length - 1;
        }

        var url = new StringBuilder();
        for (var i = startIndex; i < endIndex; i++) {
            url.Append(parts[i]);
        }
        spec.Url = url.ToString();
        return spec;
    }

    // Trims the accumulated body and detects a `< file` / `<@ file` directive.
    private static string NormalizeBody(string raw, HttpRequestSpec spec) {
        var trimmed = raw.Trim('\n').TrimEnd();
        if (trimmed.Length == 0) {
            return null;
        }

        var firstLine = trimmed.Split('\n')[0].Trim();
        if (firstLine.StartsWith("<", StringComparison.Ordinal)) {
            var rest = firstLine.Substring(1);
            var interpolate = false;
            if (rest.StartsWith("@", StringComparison.Ordinal)) {
                interpolate = true;
                rest = rest.Substring(1);
                // `<@encoding path` — drop a leading encoding token if two tokens remain.
                var tokens = rest.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 2) {
                    rest = tokens[1];
                }
            }
            var path = rest.Trim();
            if (path.Length > 0) {
                spec.BodyFromFile = path;
                spec.BodyFileInterpolate = interpolate;
                return null;
            }
        }

        return trimmed;
    }
}
