using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace ClrKernel.Runner;

/// <summary>
/// An ordered set of notebook parameters, each rendered to a C# literal.
/// Papermill-style: the runner injects a cell of <c>var name = literal;</c>
/// statements after the notebook's <c>// parameters</c> cell (or at the top when
/// there is none), so both direct variable use and the
/// <c>GetVariable&lt;T&gt;(name, default)</c> pattern observe the supplied values.
///
/// Sources are layered: file (<c>-f</c>) and inline YAML (<c>-y</c>) form the base,
/// then <c>-p</c>/<c>-r</c> overrides are applied — for any given name, the last
/// value set wins.
/// </summary>
public class RunnerParameters {
    // Insertion-ordered so the injected cell is stable and readable.
    private readonly Dictionary<string, string> _literals = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    private static readonly Regex _identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Number of parameters collected.</summary>
    public int Count => _order.Count;

    /// <summary>The rendered C# literal for a parameter (for tests/inspection).</summary>
    public string LiteralFor(string name) => _literals.TryGetValue(name, out var v) ? v : null;

    private void SetLiteral(string name, string literal) {
        ValidateName(name);
        if (!_literals.ContainsKey(name)) {
            _order.Add(name);
        }
        _literals[name] = literal;
    }

    private static void ValidateName(string name) {
        if (string.IsNullOrWhiteSpace(name) || !_identifier.IsMatch(name)) {
            throw new ArgumentException($"'{name}' is not a valid C# parameter name.");
        }
    }

    /// <summary>Adds a <c>-p</c> parameter, inferring its type from the text.</summary>
    public void SetInferred(string name, string rawValue) => SetLiteral(name, InferScalarLiteral(rawValue));

    /// <summary>Adds a <c>-r</c> parameter, always treated as a raw string.</summary>
    public void SetRaw(string name, string rawValue) => SetLiteral(name, StringLiteral(rawValue));

    /// <summary>
    /// Merges a YAML or JSON document (from <c>-f</c> or <c>-y</c>). The top level
    /// must be a mapping; each key becomes a parameter and scalar values are
    /// type-inferred (bool/int/long/double/string), sequences become
    /// <c>object[]</c>, and nested mappings become <c>Dictionary&lt;string, object&gt;</c>.
    /// </summary>
    public void MergeYaml(string yamlOrJson) {
        object graph;
        try {
            graph = new Deserializer().Deserialize<object>(yamlOrJson);
        } catch (Exception e) {
            throw new ArgumentException($"Could not parse parameters as YAML/JSON: {e.Message}", e);
        }
        if (graph is null) {
            return;
        }
        if (graph is not IDictionary<object, object> map) {
            throw new ArgumentException("Parameters document must be a mapping of name to value at the top level.");
        }
        foreach (var entry in map) {
            var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            SetLiteral(name, RenderNode(entry.Value));
        }
    }

    /// <summary>
    /// Renders the injected parameters cell, or null when there are no parameters.
    /// </summary>
    public string RenderCell() {
        if (_order.Count == 0) {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine("// clrkernel:injected-parameters");
        foreach (var name in _order) {
            sb.Append("var ").Append(name).Append(" = ").Append(_literals[name]).AppendLine(";");
        }
        return sb.ToString();
    }

    // --- rendering helpers -------------------------------------------------

    private static string RenderNode(object node) {
        switch (node) {
            case null:
                return "null";
            case IDictionary<object, object> map: {
                    var parts = map.Select(kv =>
                        $"[{StringLiteral(Convert.ToString(kv.Key, CultureInfo.InvariantCulture))}] = {RenderNode(kv.Value)}");
                    return "new System.Collections.Generic.Dictionary<string, object> { " + string.Join(", ", parts) + " }";
                }
            case IEnumerable<object> seq:
                return "new object[] { " + string.Join(", ", seq.Select(RenderNode)) + " }";
            default:
                // YamlDotNet yields scalars as strings; infer their C# type.
                return InferScalarLiteral(Convert.ToString(node, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Infers a C# literal from a scalar string: booleans, integers (int then
    /// long), doubles, else a string. Type-inference mirrors papermill so
    /// <c>-p count 5</c> is an int and <c>-p rate 0.6</c> is a double.
    /// </summary>
    internal static string InferScalarLiteral(string value) {
        if (value is null) {
            return "null";
        }
        var trimmed = value.Trim();

        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) {
            return "true";
        }
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) {
            return "false";
        }
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) {
            return i.ToString(CultureInfo.InvariantCulture);
        }
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) {
            return l.ToString(CultureInfo.InvariantCulture) + "L";
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) {
            var rendered = d.ToString("R", CultureInfo.InvariantCulture);
            // Ensure it is a double literal, not an int one (e.g. "3" -> "3d").
            if (rendered.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) {
                rendered += "d";
            }
            return rendered;
        }
        return StringLiteral(value);
    }

    /// <summary>Renders a C# double-quoted string literal with the needed escapes.</summary>
    internal static string StringLiteral(string value) {
        if (value is null) {
            return "null";
        }
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value) {
            switch (c) {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    } else {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
