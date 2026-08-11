using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClrKernel.Language.Http;

/// <summary>
/// Resolves <c>{{…}}</c> placeholders in a <c>.http</c> document against three
/// sources, in priority order: named-response references
/// (<c>{{login.response.body.$.token}}</c>), system variables
/// (<c>{{$guid}}</c>, <c>{{$timestamp}}</c>, …), and file variables
/// (<c>@name = value</c>). File variables may reference one another; cycles and
/// unknown names resolve to the literal placeholder so problems are visible.
/// </summary>
public sealed class HttpVariableResolver {
    private static readonly Regex _placeholder = new(@"\{\{(?<expr>.*?)\}\}", RegexOptions.Compiled);
    private readonly Random _random = new();

    private readonly Dictionary<string, string> _fileVariables;
    private readonly IReadOnlyDictionary<string, HttpExchange> _responses;

    public HttpVariableResolver(
        IEnumerable<HttpVariableDefinition> fileVariables,
        IReadOnlyDictionary<string, HttpExchange> responses) {
        _fileVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fileVariables != null) {
            foreach (var v in fileVariables) {
                _fileVariables[v.Name] = v.Value; // later definitions win
            }
        }
        _responses = responses ?? new Dictionary<string, HttpExchange>();
    }

    /// <summary>Substitutes every <c>{{…}}</c> placeholder in <paramref name="input"/>.</summary>
    public string Resolve(string input) => Resolve(input, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string Resolve(string input, HashSet<string> resolving) {
        if (string.IsNullOrEmpty(input)) {
            return input;
        }
        return _placeholder.Replace(input, match => {
            var expr = match.Groups["expr"].Value.Trim();
            var value = ResolveExpression(expr, resolving);
            return value ?? match.Value; // leave the literal placeholder when unresolved
        });
    }

    private string ResolveExpression(string expr, HashSet<string> resolving) {
        if (expr.Length == 0) {
            return null;
        }
        if (expr[0] == '$') {
            return ResolveSystemVariable(expr);
        }

        var dot = expr.IndexOf('.');
        if (dot > 0) {
            var head = expr.Substring(0, dot);
            if (_responses.ContainsKey(head)) {
                return ResolveResponseReference(head, expr.Substring(dot + 1));
            }
        }

        // File variable — resolve its value recursively (it may hold placeholders).
        if (_fileVariables.TryGetValue(expr, out var raw)) {
            if (!resolving.Add(expr)) {
                return null; // cycle
            }
            try {
                return Resolve(raw, resolving);
            } finally {
                resolving.Remove(expr);
            }
        }

        return null;
    }

    // --- system variables --------------------------------------------------

    private string ResolveSystemVariable(string expr) {
        var parts = expr.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];

        switch (name.ToLowerInvariant()) {
            case "$guid":
                return Guid.NewGuid().ToString();

            case "$randomint": {
                    var min = parts.Length > 1 && int.TryParse(parts[1], out var lo) ? lo : 0;
                    var max = parts.Length > 2 && int.TryParse(parts[2], out var hi) ? hi : int.MaxValue;
                    if (min > max) {
                        (min, max) = (max, min);
                    }
                    return _random.Next(min, max).ToString(CultureInfo.InvariantCulture);
                }

            case "$timestamp": {
                    var time = ApplyOffset(DateTimeOffset.UtcNow, parts, 1);
                    return time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                }

            case "$datetime":
                return FormatDateTime(DateTimeOffset.UtcNow, parts);

            case "$localdatetime":
                return FormatDateTime(DateTimeOffset.Now, parts);

            case "$processenv":
                return parts.Length > 1 ? Environment.GetEnvironmentVariable(parts[1]) ?? string.Empty : string.Empty;

            default:
                return null;
        }
    }

    // {{$datetime rfc1123|iso8601|"custom format" [offset unit]}}
    private static string FormatDateTime(DateTimeOffset now, string[] parts) {
        var format = parts.Length > 1 ? parts[1] : "iso8601";
        var time = ApplyOffset(now, parts, 2);
        switch (format.ToLowerInvariant()) {
            case "rfc1123":
                return time.ToString("r", CultureInfo.InvariantCulture);
            case "iso8601":
                return time.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);
            default:
                var custom = format.Trim('"', '\'');
                try {
                    return time.ToString(custom, CultureInfo.InvariantCulture);
                } catch (FormatException) {
                    return time.ToString("o", CultureInfo.InvariantCulture);
                }
        }
    }

    // Applies a trailing "[+|-]N unit" offset (e.g. "-1 h", "2 d") starting at parts[startIndex].
    private static DateTimeOffset ApplyOffset(DateTimeOffset now, string[] parts, int startIndex) {
        if (parts.Length <= startIndex + 1) {
            return now;
        }
        if (!int.TryParse(parts[startIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)) {
            return now;
        }
        var unit = parts[startIndex + 1].ToLowerInvariant();
        switch (unit) {
            case "ms": return now.AddMilliseconds(amount);
            case "s": return now.AddSeconds(amount);
            case "m": return now.AddMinutes(amount);
            case "h": return now.AddHours(amount);
            case "d": return now.AddDays(amount);
            case "w": return now.AddDays(amount * 7);
            case "mo": return now.AddMonths(amount);
            case "y": return now.AddYears(amount);
            default: return now;
        }
    }

    // --- response references -----------------------------------------------

    // tail is e.g. "response.body.$.token" or "response.headers.Location".
    private string ResolveResponseReference(string name, string tail) {
        if (!_responses.TryGetValue(name, out var exchange) || exchange.IsError) {
            return null;
        }

        var segments = tail.Split('.');
        if (segments.Length < 2 || !segments[0].Equals("response", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var kind = segments[1].ToLowerInvariant();
        if (kind == "headers") {
            if (segments.Length < 3) {
                return null;
            }
            var header = string.Join(".", segments, 2, segments.Length - 2);
            foreach (var h in exchange.ResponseHeaders) {
                if (h.Name.Equals(header, StringComparison.OrdinalIgnoreCase)) {
                    return h.Value;
                }
            }
            return null;
        }

        if (kind == "body") {
            var path = segments.Length > 2 ? string.Join(".", segments, 2, segments.Length - 2) : "*";
            return ResolveBodyPath(exchange.BodyText, path);
        }

        return null;
    }

    private static string ResolveBodyPath(string body, string path) {
        if (body == null) {
            return null;
        }
        if (path == "*" || path == "$") {
            return body;
        }

        JsonNode root;
        try {
            root = JsonNode.Parse(body);
        } catch (JsonException) {
            return null;
        }

        var node = root;
        foreach (var accessor in TokenizePath(path)) {
            if (node == null) {
                return null;
            }
            if (accessor.IsIndex) {
                node = node is JsonArray array && accessor.Index >= 0 && accessor.Index < array.Count
                    ? array[accessor.Index]
                    : null;
            } else {
                node = node is JsonObject obj && obj.TryGetPropertyValue(accessor.Name, out var child)
                    ? child
                    : null;
            }
        }

        if (node == null) {
            return null;
        }
        return node is JsonValue value ? value.ToString() : node.ToJsonString();
    }

    private readonly struct PathAccessor {
        public PathAccessor(string name) {
            Name = name;
            Index = -1;
            IsIndex = false;
        }

        public PathAccessor(int index) {
            Name = null;
            Index = index;
            IsIndex = true;
        }

        public string Name { get; }
        public int Index { get; }
        public bool IsIndex { get; }
    }

    // "$.store.book[0].title" -> [store, book, [0], title]. A leading $ is dropped.
    private static IEnumerable<PathAccessor> TokenizePath(string path) {
        var buffer = new StringBuilder();
        var accessors = new List<PathAccessor>();

        void FlushName() {
            if (buffer.Length > 0) {
                accessors.Add(new PathAccessor(buffer.ToString()));
                buffer.Clear();
            }
        }

        foreach (var ch in path) {
            if (ch == '$') {
                continue;
            }
            if (ch == '.') {
                FlushName();
            } else if (ch == '[') {
                FlushName();
            } else if (ch == ']') {
                if (int.TryParse(buffer.ToString(), out var index)) {
                    accessors.Add(new PathAccessor(index));
                }
                buffer.Clear();
            } else {
                buffer.Append(ch);
            }
        }
        FlushName();
        return accessors;
    }
}
