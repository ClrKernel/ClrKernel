using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;
/// <summary>Parses the <c>#!sql-connect</c> magic and the per-cell connection selector.</summary>
public static class SqlDirectives {
    /// <summary>The declarative shape of the bare <c>#!sql</c> selector line.</summary>
    public static readonly DirectiveDefinition CellDefinition = new() {
        Selector = "#!sql",
        Description = "Runs the cell as T-SQL on a registered connection.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--connections", Aliases = new[] { "--connection", "-c" }, ValueRole = "connection", Description = "Connection to run on (default connection when omitted)." },
        },
    };

    /// <summary>The declarative shape of <c>#!sql-connect</c> — the single source of
    /// truth for parsing, completions, and the RPC-served language descriptor.</summary>
    public static readonly DirectiveDefinition ConnectDefinition = new() {
        Selector = "#!sql-connect",
        Description = "Registers a named SQL Server connection for #!sql cells.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true, Description = "Connection name." },
            new() { Name = "--server", Aliases = new[] { "--host", "-s" }, Description = "Server host name." },
            new() { Name = "--database", Aliases = new[] { "-d" }, Description = "Database to open." },
            new() { Name = "--user", Aliases = new[] { "--username", "-u" }, Description = "Login user name." },
            new() { Name = "--secret", Aliases = new[] { "--secret-ref" }, Description = "Secret reference for the password (credential store / CLRKERNEL_SECRET_*)." },
            new() { Name = "--connection-string", Aliases = new[] { "--cs" }, Description = "Raw connection string (carries its own auth)." },
            new() { Name = "--provider", Description = "ADO.NET provider override." },
            new() { Name = "--encrypt", EnumValues = new[] { "true", "false" }, Description = "Encrypt the connection (default true)." },
            new() { Name = "--trust-cert", Aliases = new[] { "--trust-server-certificate" }, Kind = DirectiveParameterKind.Flag, Description = "Trust the server certificate." },
            new() { Name = "--default", Kind = DirectiveParameterKind.Flag, Description = "Make this the default connection." },
            new() { Name = "--var", Aliases = new[] { "--variable", "--as" }, Description = "C# identifier to bind the connection to." },
            new() { Name = "--no-var", Aliases = new[] { "--no-variable" }, Kind = DirectiveParameterKind.Flag, Description = "Suppress the automatic C# variable binding." },
            new() { Name = "--auth", Aliases = new[] { "-a" },
                EnumValues = new[] { "sql", "integrated", "entra", "entra-password", "entra-interactive" },
                ValueDetail = "auth mode", Description = "Authentication mode." },
            new() { Name = "--option", Kind = DirectiveParameterKind.KeyValue, Repeatable = true, Description = "Extra connection-string option (key=value)." },
            new() { Name = "--password", Aliases = new[] { "-p" }, Kind = DirectiveParameterKind.Forbidden,
                ForbiddenMessage = "Passwords must not be placed in notebook cells. Store the password from the " +
                    "SQL connection panel (or a --secret reference / environment variable) instead." },
        },
    };

    /// <summary>Every SQL directive's shape, in the order pickers should list them.</summary>
    public static IReadOnlyList<DirectiveDefinition> AllDefinitions { get; } = new[] {
        CellDefinition, ConnectDefinition,
        SqlEtlDirectives.BulkDefinition, SqlEtlDirectives.MergeDefinition,
        SqlOrchestrationDirectives.RunDefinition, SqlOrchestrationDirectives.DeployDefinition,
    };

    // Flags that *shape* a connection: a directive carrying none of them merely
    // references an existing (registered / config-loaded) connection by name.
    private static readonly string[] _shapingFlags = {
        "--server", "--database", "--user", "--secret", "--connection-string",
        "--provider", "--encrypt", "--trust-cert", "--auth", "--option",
    };

    /// <summary>
    /// Parses a <c>#!sql-connect</c> line into a spec. Supported flags:
    /// <c>--name</c>, <c>--server</c>/<c>--host</c>, <c>--database</c>,
    /// <c>--auth</c> (sql|integrated|entra|entra-password|entra-interactive),
    /// <c>--user</c>, <c>--secret</c>, <c>--connection-string</c>,
    /// <c>--encrypt true|false</c>, <c>--trust-cert</c>, <c>--default</c>,
    /// <c>--option k=v</c>. A committed <c>--password</c> is rejected on purpose.
    /// </summary>
    public static SqlConnectDirective ParseConnect(string line) {
        var args = DirectiveParser.Parse(ConnectDefinition, line);
        var spec = new SqlConnectionSpec { Name = args.Get("--name") };
        if (args.Has("--server")) {
            spec.Server = args.Get("--server");
        }
        if (args.Has("--database")) {
            spec.Database = args.Get("--database");
        }
        if (args.Has("--user")) {
            spec.User = args.Get("--user");
        }
        if (args.Has("--secret")) {
            spec.SecretRef = args.Get("--secret");
        }
        if (args.Has("--connection-string")) {
            spec.RawConnectionString = args.Get("--connection-string");
        }
        if (args.Has("--provider")) {
            spec.Provider = args.Get("--provider");
        }
        if (args.Has("--encrypt")) {
            spec.Encrypt = ParseBool(args.Get("--encrypt"));
        }
        if (args.Has("--trust-cert")) {
            spec.TrustServerCertificate = true;
        }
        foreach (var kv in args.KeyValues("--option")) {
            spec.ExtraOptions[kv.Key] = kv.Value;
        }
        var authExplicit = args.Has("--auth");
        if (authExplicit) {
            spec.Auth = ParseAuth(args.Get("--auth"));
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
        var variable = ResolveVariable(args.Get("--var"), args.HasAny("--var", "--no-var"), spec.Name);
        // Only --name (plus --default/--var) means "reference an existing connection"
        // — one loaded from connections.json or registered earlier — rather than
        // "define a new one". Registering the bare spec would clobber the real one.
        return new SqlConnectDirective(spec, args.Has("--default"), variable, isReference: !args.HasAny(_shapingFlags));
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

    /// <summary>
    /// The first value on a directive comment's argument — one token, or one quoted
    /// phrase.
    ///
    /// <para>
    /// Quoted, because a connection is named by a person: "Warehouse (dev)" is a
    /// name somebody has, and splitting on the space asked for a connection called
    /// <c>Warehouse</c> and reported it missing. The quotes come off, so an unquoted
    /// single-word name behaves exactly as before.
    /// </para>
    /// </summary>
    private static string FirstToken(string s) {
        var trimmed = (s ?? string.Empty).Trim();
        if (trimmed.StartsWith('"') || trimmed.StartsWith('\'')) {
            return DirectiveParser.Tokenize(trimmed).FirstOrDefault();
        }
        return trimmed.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    /// <summary>Reads the connection name from a <c>#!sql --connections name</c>
    /// selector line, or null when absent.</summary>
    public static string SelectorConnection(string selectorLine) => ExtractConnectionFlag(selectorLine ?? string.Empty);

    private static string ExtractConnectionFlag(string line) =>
        DirectiveParser.FindValue(line, "--connections", "--connection", "-c");
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
