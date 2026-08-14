using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClrKernel.Language.Shell;

/// <summary>Parses <c>#!shell-connect</c> and the per-cell <c>--connection</c> flag.</summary>
public static class ShellDirectives {
    /// <summary>
    /// Parses a <c>#!shell-connect</c> line. Flags: <c>--name</c>, <c>--host</c>,
    /// <c>--user</c>, <c>--port</c>, <c>--identity</c>. A <c>--password</c> is rejected
    /// on purpose — SSH auth is key-based (agent and ~/.ssh/config apply).
    /// </summary>
    public static ShellConnectionSpec ParseConnect(string line) {
        var tokens = Tokenize(StripSelector(line, "#!shell-connect"));
        var spec = new ShellConnectionSpec();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--name": case "-n": spec.Name = Next(); break;
                case "--host": case "--server": case "-h": spec.Host = Next(); break;
                case "--user": case "--username": case "-u": spec.User = Next(); break;
                case "--port":
                case "-p":
                    spec.Port = int.TryParse(Next(), out var port)
                        ? port
                        : throw new FormatException("--port expects a number.");
                    break;
                case "--identity": case "-i": spec.IdentityFile = Next(); break;
                case "--remote-shell": case "--shell": spec.RemoteShell = Next().ToLowerInvariant(); break;
                case "--password":
                    throw new FormatException(
                        "SSH targets use key authentication (your keys, agent, and ~/.ssh/config apply); " +
                        "passwords are not supported and must never be placed in a notebook.");
                default:
                    throw new FormatException($"Unknown #!shell-connect flag '{t}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(spec.Name)) {
            throw new FormatException("#!shell-connect requires --name.");
        }
        if (string.IsNullOrWhiteSpace(spec.Host)) {
            throw new FormatException("#!shell-connect requires --host.");
        }
        return spec;
    }

    private static readonly Regex _connectionFlag = new(
        @"--connection\s+(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The <c>--connection &lt;name&gt;</c> from a selector line, or null (local).</summary>
    public static string SelectorConnection(string firstLine) {
        var match = _connectionFlag.Match(firstLine ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string StripSelector(string line, string selector) {
        var trimmed = (line ?? string.Empty).Trim();
        return trimmed.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(selector.Length)
            : trimmed;
    }

    // Whitespace tokenizer with double/single-quote support (identity paths with spaces).
    private static List<string> Tokenize(string text) {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var ch in text ?? string.Empty) {
            if (quote != '\0') {
                if (ch == quote) {
                    quote = '\0';
                } else {
                    current.Append(ch);
                }
            } else if (ch == '"' || ch == '\'') {
                quote = ch;
            } else if (char.IsWhiteSpace(ch)) {
                if (current.Length > 0) {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            } else {
                current.Append(ch);
            }
        }
        if (current.Length > 0) {
            tokens.Add(current.ToString());
        }
        return tokens;
    }
}
