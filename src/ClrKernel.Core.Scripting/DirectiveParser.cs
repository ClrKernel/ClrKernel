using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// The one tokenizer and flag binder every cell language's <c>#!</c> directives go
/// through. Replaces the per-language hand-written copies; semantic validation
/// (auth ladders, identifier checks, int/bool conversion) stays in each language's
/// post-bind code so its exact error messages survive.
/// </summary>
public static class DirectiveParser {
    /// <summary>
    /// Splits a flag line into tokens, honoring double and single quotes. Quotes
    /// glue onto adjacent characters within a token, and a quoted empty string is
    /// a real (empty) token — so <c>--server ""</c> binds an empty value rather
    /// than swallowing the next flag.
    /// </summary>
    public static List<string> Tokenize(string input) {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) {
            return tokens;
        }
        var sb = new StringBuilder();
        char quote = '\0';
        bool inToken = false;
        foreach (var c in input) {
            if (quote != '\0') {
                if (c == quote) {
                    quote = '\0';
                } else {
                    sb.Append(c);
                }
                inToken = true;
            } else if (c == '"' || c == '\'') {
                quote = c;
                inToken = true;
            } else if (char.IsWhiteSpace(c)) {
                if (inToken) {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }
            } else {
                sb.Append(c);
                inToken = true;
            }
        }
        if (inToken) {
            tokens.Add(sb.ToString());
        }
        return tokens;
    }

    /// <summary>Drops a leading <paramref name="selector"/> (case-insensitive) from the line.</summary>
    public static string StripSelector(string line, string selector) {
        var t = (line ?? string.Empty).TrimStart();
        return t.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
            ? t.Substring(selector.Length)
            : t;
    }

    /// <summary>
    /// Binds a directive line against its definition. Unknown tokens, missing
    /// values, forbidden flags, malformed key=value pairs, and missing required
    /// parameters all throw <see cref="FormatException"/> with the messages
    /// notebooks have always seen.
    /// </summary>
    public static DirectiveArgs Parse(DirectiveDefinition definition, string line) {
        var tokens = Tokenize(StripSelector(line, definition.Selector));
        var args = new DirectiveArgs();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            var parameter = definition.Find(t)
                ?? throw new FormatException($"Unknown {definition.Selector} flag '{t}'.");
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (parameter.Kind) {
                case DirectiveParameterKind.Forbidden:
                    throw new FormatException(parameter.ForbiddenMessage);
                case DirectiveParameterKind.Flag:
                    args.MarkPresent(parameter.Name);
                    break;
                case DirectiveParameterKind.KeyValue: {
                        var kv = Next();
                        var eq = kv.IndexOf('=');
                        if (eq <= 0) {
                            throw new FormatException($"{parameter.Name} expects {parameter.KeyValueHint}, got '{kv}'.");
                        }
                        args.AddKeyValue(parameter.Name, kv.Substring(0, eq), kv.Substring(eq + 1));
                        break;
                    }
                default:
                    args.AddValue(parameter.Name, Next());
                    break;
            }
        }
        foreach (var parameter in definition.Parameters) {
            if (parameter.Required && string.IsNullOrWhiteSpace(args.Get(parameter.Name))) {
                throw new FormatException($"{definition.Selector} requires {parameter.RequiredLabel ?? parameter.Name}.");
            }
        }
        return args;
    }

    /// <summary>
    /// Tolerant scan for one flag's value on a line that may not fully parse —
    /// the per-cell <c>--connection name</c> selector and DAX's pre-parse
    /// <c>--secret</c> lookup. Returns the token after the first match, or null.
    /// </summary>
    public static string FindValue(string line, params string[] flagNames) {
        var tokens = Tokenize(line ?? string.Empty);
        for (var i = 0; i < tokens.Count - 1; i++) {
            if (flagNames.Any(f => string.Equals(f, tokens[i], StringComparison.OrdinalIgnoreCase))) {
                return tokens[i + 1];
            }
        }
        return null;
    }
}

/// <summary>The bound result of <see cref="DirectiveParser.Parse"/>, keyed by canonical flag name.</summary>
public sealed class DirectiveArgs {
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _keyValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    internal void MarkPresent(string name) {
        _present.Add(name);
        _order.Add(name);
    }

    internal void AddValue(string name, string value) {
        MarkPresent(name);
        if (!_values.TryGetValue(name, out var list)) {
            _values[name] = list = new List<string>();
        }
        list.Add(value);
    }

    internal void AddKeyValue(string name, string key, string value) {
        MarkPresent(name);
        if (!_keyValues.TryGetValue(name, out var map)) {
            _keyValues[name] = map = new Dictionary<string, string>();
        }
        map[key] = value;
    }

    /// <summary>The last bound value for a flag, or null. Repeated flags: last one wins.</summary>
    public string Get(string name) => _values.TryGetValue(name, out var list) ? list[list.Count - 1] : null;

    /// <summary>Every bound value for a repeatable flag (empty when absent).</summary>
    public IReadOnlyList<string> GetAll(string name) =>
        _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();

    /// <summary>True when the flag appeared at all (switches and valued flags alike).</summary>
    public bool Has(string name) => _present.Contains(name);

    /// <summary>True when any of the named flags appeared.</summary>
    public bool HasAny(params string[] names) => names.Any(Has);

    /// <summary>The last-seen flag among <paramref name="names"/>, or null — how a
    /// mutually exclusive pair like <c>--ssh</c>/<c>--winrm</c> keeps last-one-wins.</summary>
    public string LastOf(params string[] names) =>
        _order.LastOrDefault(n => names.Contains(n, StringComparer.Ordinal));

    /// <summary>The accumulated <c>k=v</c> pairs of a KeyValue flag (empty when absent).</summary>
    public IReadOnlyDictionary<string, string> KeyValues(string name) =>
        _keyValues.TryGetValue(name, out var map) ? map : _empty;

    private static readonly Dictionary<string, string> _empty = new();
}
