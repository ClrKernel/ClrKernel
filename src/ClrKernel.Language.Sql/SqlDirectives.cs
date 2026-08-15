using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;
/// <summary>Parses the <c>#!sql-connect</c> magic and the per-cell connection selector.</summary>
public static class SqlDirectives {
    /// <summary>
    /// Parses a <c>#!sql-connect</c> line into a spec. Supported flags:
    /// <c>--name</c>, <c>--server</c>/<c>--host</c>, <c>--database</c>,
    /// <c>--auth</c> (sql|integrated|entra|entra-password|entra-interactive),
    /// <c>--user</c>, <c>--secret</c>, <c>--connection-string</c>,
    /// <c>--encrypt true|false</c>, <c>--trust-cert</c>, <c>--default</c>,
    /// <c>--option k=v</c>. A committed <c>--password</c> is rejected on purpose.
    /// </summary>
    public static SqlConnectDirective ParseConnect(string line) {
        var body = StripLeadingSelector(line, "#!sql-connect");
        var tokens = Tokenize(body);
        var spec = new SqlConnectionSpec();
        bool isDefault = false;
        bool authExplicit = false;
        string explicitVariable = null;
        bool variableFlagSeen = false;
        bool specFlagSeen = false; // any flag that *shapes* a connection (vs. referencing one)

        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--name": case "-n": spec.Name = Next(); break;
                case "--server": case "--host": case "-s": case "-S": spec.Server = Next(); specFlagSeen = true; break;
                case "--database": case "-d": case "-D": spec.Database = Next(); specFlagSeen = true; break;
                case "--user": case "--username": case "-u": case "-U": spec.User = Next(); specFlagSeen = true; break;
                case "--secret": case "--secret-ref": spec.SecretRef = Next(); specFlagSeen = true; break;
                case "--connection-string": case "--cs": spec.RawConnectionString = Next(); specFlagSeen = true; break;
                case "--provider": spec.Provider = Next(); specFlagSeen = true; break;
                case "--encrypt": spec.Encrypt = ParseBool(Next()); specFlagSeen = true; break;
                case "--trust-cert": case "--trust-server-certificate": spec.TrustServerCertificate = true; specFlagSeen = true; break;
                case "--default": isDefault = true; break;
                case "--var":
                case "--variable":
                case "--as":
                    explicitVariable = Next();
                    variableFlagSeen = true;
                    break;
                case "--no-var": case "--no-variable": variableFlagSeen = true; break;
                case "--auth": case "-a": spec.Auth = ParseAuth(Next()); authExplicit = true; specFlagSeen = true; break;
                case "--option": {
                        var kv = Next();
                        var eq = kv.IndexOf('=');
                        if (eq <= 0) {
                            throw new FormatException($"--option expects key=value, got '{kv}'.");
                        }
                        spec.ExtraOptions[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                        specFlagSeen = true;
                        break;
                    }
                case "--password":
                case "-p":
                    throw new FormatException(
                        "Passwords must not be placed in notebook cells. Store the password from the " +
                        "SQL connection panel (or a --secret reference / environment variable) instead.");
                default:
                    throw new FormatException($"Unknown #!sql-connect flag '{t}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(spec.Name)) {
            throw new FormatException("#!sql-connect requires --name.");
        }
        // Default auth: a raw connection string carries its own auth; else a SQL
        // login if a user was named; otherwise Integrated.
        if (!authExplicit) {
            if (!string.IsNullOrWhiteSpace(spec.RawConnectionString) && string.IsNullOrWhiteSpace(spec.User)) {
                spec.Auth = SqlAuthMode.RawConnectionString;
            } else if (!string.IsNullOrWhiteSpace(spec.User)) {
                spec.Auth = SqlAuthMode.SqlPassword;
            }
        }
        var variable = ResolveVariable(explicitVariable, variableFlagSeen, spec.Name);
        // Only --name (plus --default/--var) means "reference an existing connection"
        // — one loaded from connections.json or registered earlier — rather than
        // "define a new one". Registering the bare spec would clobber the real one.
        return new SqlConnectDirective(spec, isDefault, variable, isReference: !specFlagSeen);
    }

    // Decides the C# variable to bind: an explicit --var (validated), else the
    // connection --name when it is a valid, non-keyword identifier, else none.
    private static string ResolveVariable(string explicitVariable, bool variableFlagSeen, string name) {
        if (!string.IsNullOrEmpty(explicitVariable)) {
            if (!IsValidIdentifier(explicitVariable)) {
                throw new FormatException($"--var '{explicitVariable}' is not a valid C# identifier.");
            }
            return explicitVariable;
        }
        if (variableFlagSeen) {
            return null; // --no-var: suppress the auto binding
        }
        return IsValidIdentifier(name) && !_cSharpKeywords.Contains(name) ? name : null;
    }

    internal static bool IsValidIdentifier(string s) {
        if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) {
            return false;
        }
        for (var i = 1; i < s.Length; i++) {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) {
                return false;
            }
        }
        return true;
    }

    private static readonly HashSet<string> _cSharpKeywords = new HashSet<string>(StringComparer.Ordinal) {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
        "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "var",
    };

    private static SqlAuthMode ParseAuth(string value) {
        switch (value.ToLowerInvariant()) {
            case "sql": case "sqlpassword": case "password": return SqlAuthMode.SqlPassword;
            case "integrated": case "windows": case "trusted": return SqlAuthMode.Integrated;
            case "aad": case "entra": case "aad-default": case "entra-default": return SqlAuthMode.AzureAdDefault;
            case "aad-password": case "entra-password": return SqlAuthMode.AzureAdPassword;
            case "aad-interactive": case "entra-interactive": case "interactive": return SqlAuthMode.AzureAdInteractive;
            default: throw new FormatException($"Unknown --auth value '{value}'.");
        }
    }

    private static bool ParseBool(string value) {
        switch (value.ToLowerInvariant()) {
            case "true": case "yes": case "1": case "on": return true;
            case "false": case "no": case "0": case "off": return false;
            default: throw new FormatException($"Expected true/false, got '{value}'.");
        }
    }

    /// <summary>
    /// Determines which registered connection a <c>#!sql</c> cell targets. The
    /// selector may be an inline <c>#!sql --connections name</c>, or a leading
    /// SQL comment line <c>-- connections name</c> / <c>--connection name</c>
    /// (which stays valid T-SQL). Returns null name when none is present, so
    /// the caller uses the default connection.
    /// </summary>
    public static SqlCellRequest ParseCell(string cellBody) {
        var text = cellBody ?? string.Empty;
        string connection = null;
        string step = null;
        var needs = new List<string>();

        // Scan the leading block of blank / comment / #!sql lines for directives.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines) {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (trimmed.StartsWith("#!sql", StringComparison.OrdinalIgnoreCase)) {
                connection ??= ExtractConnectionFlag(trimmed);
                continue;
            }
            if (!trimmed.StartsWith("--")) {
                break; // first real SQL line ends directive scanning
            }
            var rest = trimmed.Substring(2).Trim();
            if (MatchKeyword(rest, "connections", "connection", out var connArg)) {
                connection ??= FirstToken(connArg);
            } else if (MatchKeyword(rest, "step", null, out var stepArg)) {
                step ??= FirstToken(stepArg);
            } else if (MatchKeyword(rest, "needs", "depends-on", out var needsArg)) {
                needs.AddRange(needsArg.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
            // other comments are ignored but don't stop the scan
        }

        return new SqlCellRequest(connection, text) {
            StepName = step,
            Needs = needs,
        };
    }

    private static bool MatchKeyword(string rest, string kw, string alt, out string arg) {
        foreach (var k in alt == null ? new[] { kw } : new[] { kw, alt }) {
            if (rest.StartsWith(k, StringComparison.OrdinalIgnoreCase) &&
                (rest.Length == k.Length || !char.IsLetterOrDigit(rest[k.Length]))) {
                arg = rest.Substring(k.Length).TrimStart(':', '=', ' ', '\t').Trim();
                return true;
            }
        }
        arg = null;
        return false;
    }

    private static string FirstToken(string s) =>
        s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// <summary>Reads the connection name from a <c>#!sql --connections name</c>
    /// selector line, or null when absent.</summary>
    public static string SelectorConnection(string selectorLine) => ExtractConnectionFlag(selectorLine ?? string.Empty);

    private static string ExtractConnectionFlag(string line) {
        var tokens = Tokenize(StripLeadingSelector(line, "#!sql"));
        for (var i = 0; i < tokens.Count - 1; i++) {
            var t = tokens[i].ToLowerInvariant();
            if (t == "--connections" || t == "--connection" || t == "-c") {
                return tokens[i + 1];
            }
        }
        return null;
    }

    private static string StripLeadingSelector(string line, string selector) {
        var t = (line ?? string.Empty).TrimStart();
        return t.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
            ? t.Substring(selector.Length)
            : t;
    }

    /// <summary>Splits a flag line into tokens, honoring double and single quotes.</summary>
    internal static List<string> Tokenize(string input) {
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
}

/// <summary>A parsed <c>#!sql-connect</c>: the spec plus whether it is the default.</summary>
public sealed class SqlConnectDirective {
    public SqlConnectDirective(SqlConnectionSpec spec, bool isDefault, string variable = null, bool isReference = false) {
        Spec = spec;
        IsDefault = isDefault;
        Variable = variable;
        IsReference = isReference;
    }
    public SqlConnectionSpec Spec { get; }
    public bool IsDefault { get; }

    /// <summary>True when the directive only names a connection (no server, auth, or
    /// other shaping flags): use the registered/config-loaded spec of that name —
    /// binding its variable and optionally making it the default — instead of
    /// registering a new, empty one over it.</summary>
    public bool IsReference { get; }

    /// <summary>The C# identifier to bind this connection to for later <c>#!csharp</c>
    /// cells (a <c>SqlDatabase</c>), or null when none applies. Comes from
    /// <c>--var</c>, or the connection's <c>--name</c> when that is a valid identifier.</summary>
    public string Variable { get; }
}

/// <summary>A parsed <c>#!sql</c> cell: the chosen connection name (or null) and the SQL.</summary>
public sealed class SqlCellRequest {
    public SqlCellRequest(string connectionName, string sql) {
        ConnectionName = connectionName;
        Sql = sql;
    }
    public string ConnectionName { get; }
    public string Sql { get; }

    /// <summary>Present when the cell declares <c>-- step &lt;name&gt;</c> (a pipeline step).</summary>
    public string StepName { get; set; }

    /// <summary>Upstream step names from <c>-- needs a, b</c> (empty when none).</summary>
    public IReadOnlyList<string> Needs { get; set; } = new List<string>();
}
