using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClrKernel.AnalysisServices;
/// <summary>A parsed <c>#!dax-connect</c>: a named cube plus whether it is the default.</summary>
public sealed class DaxConnectDirective {
    public DaxConnectDirective(string name, SsasConnectionSpec spec, bool isDefault) {
        Name = name;
        Spec = spec;
        IsDefault = isDefault;
    }
    public string Name { get; }
    public SsasConnectionSpec Spec { get; }
    public bool IsDefault { get; }
}

/// <summary>A parsed <c>#!dax</c> cell: the chosen cube (or null) and the DAX.</summary>
public sealed class DaxCellRequest {
    public DaxCellRequest(string cubeName, string dax) {
        CubeName = cubeName;
        Dax = dax;
    }
    public string CubeName { get; }
    public string Dax { get; }
}

/// <summary>Parses the <c>#!dax-connect</c> magic and the per-cell cube selector.</summary>
public static class DaxDirectives {
    /// <summary>
    /// Parses a <c>#!dax-connect</c> line. Flags: <c>--name</c>, <c>--server</c>,
    /// <c>--database</c>, <c>--user</c>, <c>--secret</c> (an env-var name / ref),
    /// <c>--auth</c> (integrated|sql|aad), <c>--fabric --workspace W --model M</c>,
    /// <c>--azure-as</c>, <c>--connection-string</c>, <c>--default</c>. A committed
    /// <c>--password</c> is rejected.
    /// </summary>
    public static DaxConnectDirective ParseConnect(string line) {
        var tokens = Tokenize(StripSelector(line, "#!dax-connect"));
        string name = null, server = null, database = null, user = null, secret = null,
               auth = null, workspace = null, model = null, connectionString = null;
        bool fabric = false, azureAs = false, isDefault = false;

        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--name": case "-n": name = Next(); break;
                case "--server": case "--host": case "-s": server = Next(); break;
                case "--database": case "-d": database = Next(); break;
                case "--user": case "--username": case "-u": user = Next(); break;
                case "--secret": case "--secret-ref": secret = Next(); break;
                case "--auth": case "-a": auth = Next().ToLowerInvariant(); break;
                case "--workspace": workspace = Next(); break;
                case "--model": case "--dataset": model = Next(); break;
                case "--connection-string": case "--cs": connectionString = Next(); break;
                case "--fabric": fabric = true; break;
                case "--azure-as": case "--aas": azureAs = true; break;
                case "--default": isDefault = true; break;
                case "--password":
                case "-p":
                    throw new FormatException(
                        "Passwords must not be placed in notebook cells. Use --secret <env-var> " +
                        "(resolved from an environment variable), Integrated auth, or Entra instead.");
                default: throw new FormatException($"Unknown #!dax-connect flag '{t}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(name)) {
            throw new FormatException("#!dax-connect requires --name.");
        }

        SsasConnectionSpec spec;
        if (!string.IsNullOrWhiteSpace(connectionString)) {
            spec = Ssas.FromConnectionString(connectionString).Spec;
        } else if (fabric || (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(model))) {
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(model)) {
                throw new FormatException("#!dax-connect --fabric requires --workspace and --model.");
            }
            spec = Ssas.ConnectFabric(workspace, model).Spec;
        } else if (azureAs || auth == "aad" || auth == "entra") {
            RequireServer(server);
            spec = Ssas.ConnectAzureAnalysisServices(server, database).Spec;
        } else if (!string.IsNullOrWhiteSpace(user) || auth == "sql" || auth == "user") {
            RequireServer(server);
            var password = string.IsNullOrWhiteSpace(secret) ? null : ResolveSecret(secret);
            spec = Ssas.Connect(server, database, user, password).Spec;
        } else {
            RequireServer(server);
            spec = Ssas.Connect(server, database).Spec;
        }

        return new DaxConnectDirective(name, spec, isDefault);
    }

    /// <summary>
    /// Determines which cube a <c>#!dax</c> cell targets: an inline
    /// <c>#!dax --connections name</c>, or a leading DAX comment
    /// <c>-- connections name</c> (valid DAX). Null cube name → default.
    /// </summary>
    public static DaxCellRequest ParseCell(string cellBody) {
        var text = cellBody ?? string.Empty;
        string cube = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n')) {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (trimmed.StartsWith("#!dax", StringComparison.OrdinalIgnoreCase)) {
                cube ??= SelectorConnection(trimmed);
                continue;
            }
            if (trimmed.StartsWith("--") || trimmed.StartsWith("//")) {
                var rest = trimmed.Substring(2).Trim();
                foreach (var kw in new[] { "connections", "connection", "cube" }) {
                    if (rest.StartsWith(kw, StringComparison.OrdinalIgnoreCase) &&
                        (rest.Length == kw.Length || !char.IsLetterOrDigit(rest[kw.Length]))) {
                        var after = rest.Substring(kw.Length).TrimStart(':', '=', ' ', '\t').Trim();
                        cube ??= after.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    }
                }
                continue;
            }
            break; // first real DAX line ends directive scanning
        }
        return new DaxCellRequest(cube, text);
    }

    /// <summary>Reads the cube name from a <c>#!dax --connections name</c> line.</summary>
    public static string SelectorConnection(string selectorLine) {
        var tokens = Tokenize(StripSelector(selectorLine ?? string.Empty, "#!dax"));
        for (var i = 0; i < tokens.Count - 1; i++) {
            var t = tokens[i].ToLowerInvariant();
            if (t == "--connections" || t == "--connection" || t == "--cube" || t == "-c") {
                return tokens[i + 1];
            }
        }
        return null;
    }

    private static void RequireServer(string server) {
        if (string.IsNullOrWhiteSpace(server)) {
            throw new FormatException("#!dax-connect requires --server (or --connection-string / --fabric).");
        }
    }

    // Resolves a secret ref from an environment variable, matching the SQL
    // convention: the ref itself, or CLRKERNEL_SECRET_<REF> (upper, non-alnum → '_').
    private static string ResolveSecret(string reference) {
        var direct = Environment.GetEnvironmentVariable(reference);
        if (!string.IsNullOrEmpty(direct)) {
            return direct;
        }
        var sb = new StringBuilder("CLRKERNEL_SECRET_");
        foreach (var c in reference) {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        var byName = Environment.GetEnvironmentVariable(sb.ToString());
        if (string.IsNullOrEmpty(byName)) {
            throw new FormatException($"No secret found for '{reference}'. Set the {sb} environment variable.");
        }
        return byName;
    }

    private static string StripSelector(string line, string selector) {
        var t = (line ?? string.Empty).TrimStart();
        return t.StartsWith(selector, StringComparison.OrdinalIgnoreCase) ? t.Substring(selector.Length) : t;
    }

    internal static List<string> Tokenize(string input) {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) {
            return tokens;
        }
        var sb = new StringBuilder();
        char quote = '\0';
        var inToken = false;
        foreach (var c in input) {
            if (quote != '\0') {
                if (c == quote) { quote = '\0'; } else { sb.Append(c); }
                inToken = true;
            } else if (c == '"' || c == '\'') {
                quote = c;
                inToken = true;
            } else if (char.IsWhiteSpace(c)) {
                if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
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
}
