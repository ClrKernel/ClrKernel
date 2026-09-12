using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// A cell language whose variables can be read and written by name — what
/// <c>#!share</c> needs on both ends. C# is the engine itself; F# is its fsi
/// session's bound values.
/// </summary>
public interface ICellVariables {
    bool TryGetVariable(string name, out object value);
    void SetVariable(string name, object value);
}

/// <summary>
/// <c>#!share --from csharp name [--as alias]</c>: copies a variable from one
/// language's session into the cell's own, the same directive Polyglot Notebooks
/// uses. A copy of the reference, not a live link — two compilers over two
/// sessions cannot share a symbol table, so the value is handed across and the
/// cell's language binds it under the name.
/// </summary>
public sealed class ShareDirective {
    public const string Selector = "#!share";

    public static readonly DirectiveDefinition Definition = new() {
        Selector = Selector,
        Description = "Copies a variable from another language's session into this cell's: #!share --from csharp name [--as alias].",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--from", Required = true, Description = "The language the variable lives in: csharp, fsharp, …" },
            new() { Name = "--as", Description = "The name to bind it under here (default: the same name)." },
        },
    };

    public string From { get; }
    public string Name { get; }
    public string As { get; }

    private ShareDirective(string from, string name, string alias) {
        From = from;
        Name = name;
        As = string.IsNullOrWhiteSpace(alias) ? name : alias;
    }

    /// <summary>Parses one <c>#!share</c> line, or returns null when the line is not one.</summary>
    public static ShareDirective TryParse(string line) {
        var trimmed = (line ?? string.Empty).Trim();
        if (!trimmed.StartsWith(Selector, StringComparison.OrdinalIgnoreCase)
            || (trimmed.Length > Selector.Length && !char.IsWhiteSpace(trimmed[Selector.Length]))) {
            return null;
        }
        var tokens = trimmed.Substring(Selector.Length).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string from = null, alias = null, name = null;
        for (var i = 0; i < tokens.Length; i++) {
            switch (tokens[i].ToLowerInvariant()) {
                case "--from":
                    from = i + 1 < tokens.Length ? tokens[++i] : null;
                    break;
                case "--as":
                    alias = i + 1 < tokens.Length ? tokens[++i] : null;
                    break;
                case "--name":
                    name = i + 1 < tokens.Length ? tokens[++i] : null;
                    break;
                default:
                    if (tokens[i].StartsWith("--", StringComparison.Ordinal)) {
                        throw new FormatException($"#!share: unknown flag '{tokens[i]}'. Usage: #!share --from <language> <name> [--as <alias>]");
                    }
                    name ??= tokens[i];
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(name)) {
            throw new FormatException("#!share needs --from <language> and a variable name: #!share --from csharp name [--as alias]");
        }
        return new ShareDirective(from, name, alias);
    }

    /// <summary>
    /// Pulls every <c>#!share</c> line out of a cell's leading directive block,
    /// blanking each so line numbers survive, and returns the rest with them.
    /// </summary>
    public static (string Statement, IReadOnlyList<ShareDirective> Shares) Extract(string statement) {
        if (statement == null || statement.IndexOf(Selector, StringComparison.OrdinalIgnoreCase) < 0) {
            return (statement, Array.Empty<ShareDirective>());
        }
        var lines = statement.Split('\n');
        var shares = new List<ShareDirective>();
        for (var i = 0; i < lines.Length; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (!trimmed.StartsWith("#!", StringComparison.Ordinal)) {
                break; // the leading directive block is over
            }
            var share = TryParse(trimmed);
            if (share != null) {
                shares.Add(share);
                lines[i] = string.Empty;
            }
        }
        return (string.Join("\n", lines), shares);
    }

    private static readonly HashSet<string> _csharpNames = new(StringComparer.OrdinalIgnoreCase) { "csharp", "c#", "cs" };

    /// <summary>Whether a <c>--from</c> value names the C# script state.</summary>
    public static bool IsCSharp(string language) => _csharpNames.Contains(language ?? string.Empty);

    /// <summary>
    /// The C# spelling of a runtime type, for the <c>var x = (T)…</c> submission that
    /// binds a shared value: generics expanded, nested types dotted, arrays kept.
    /// A type the script cannot name — anonymous, compiler-generated, non-public —
    /// becomes <c>object</c>, which still gives the cell the value.
    /// </summary>
    public static string CSharpTypeName(Type type) {
        if (type == null) {
            return "object";
        }
        if (type.IsArray) {
            return CSharpTypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }
        if (!type.IsVisible || type.IsGenericParameter
            || type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null) {
            return "object";
        }
        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (definition == typeof(Nullable<>)) {
                return CSharpTypeName(args[0]) + "?";
            }
            var name = new StringBuilder("global::").Append(definition.Namespace).Append('.');
            AppendNested(name, definition, args, 0);
            return name.ToString();
        }
        return type.FullName switch {
            "System.Int32" => "int",
            "System.String" => "string",
            "System.Boolean" => "bool",
            "System.Double" => "double",
            "System.Int64" => "long",
            "System.Decimal" => "decimal",
            "System.Object" => "object",
            _ => "global::" + type.FullName.Replace('+', '.'),
        };
    }

    // Nested generic types spell their parent's arguments first: Outer<T>.Inner<U>.
    private static int AppendNested(StringBuilder into, Type definition, Type[] args, int taken) {
        if (definition.DeclaringType != null) {
            taken = AppendNested(into, definition.DeclaringType, args, taken);
            into.Append('.');
        }
        var name = definition.Name;
        var tick = name.IndexOf('`');
        var count = tick < 0 ? 0 : int.Parse(name.Substring(tick + 1));
        into.Append(tick < 0 ? name : name.Substring(0, tick));
        if (count > 0) {
            into.Append('<').Append(string.Join(", ", args.Skip(taken).Take(count).Select(CSharpTypeName))).Append('>');
        }
        return taken + count;
    }
}
