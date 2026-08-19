using System;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.AnalysisServices;

namespace ClrKernel.Language.Dax;
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
    /// <summary>The declarative shape of <c>#!dax-connect</c>.</summary>
    public static readonly DirectiveDefinition ConnectDefinition = new() {
        Selector = "#!dax-connect",
        Description = "Registers a named semantic model / cube for #!dax cells.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true, Description = "Cube name." },
            new() { Name = "--server", Aliases = new[] { "--host", "-s" }, Description = "XMLA server (or powerbi:// / asazure:// endpoint)." },
            new() { Name = "--database", Aliases = new[] { "-d" }, Description = "Database / model." },
            new() { Name = "--user", Aliases = new[] { "--username", "-u" }, Description = "User name (SQL-style auth)." },
            new() { Name = "--secret", Aliases = new[] { "--secret-ref" }, Description = "Secret reference for the password." },
            new() { Name = "--auth", Aliases = new[] { "-a" }, EnumValues = new[] { "integrated", "sql", "user", "aad", "entra" }, Description = "Authentication mode." },
            new() { Name = "--workspace", Description = "Fabric / Power BI workspace." },
            new() { Name = "--model", Aliases = new[] { "--dataset" }, Description = "Fabric / Power BI semantic model." },
            new() { Name = "--connection-string", Aliases = new[] { "--cs" }, Description = "Raw ADOMD connection string." },
            new() { Name = "--fabric", Kind = DirectiveParameterKind.Flag, Description = "Connect to a Fabric / Power BI workspace (needs --workspace and --model)." },
            new() { Name = "--integrated", Aliases = new[] { "--sspi", "--windows" }, Kind = DirectiveParameterKind.Flag, Description = "Use the signed-in Windows identity." },
            new() { Name = "--azure-as", Aliases = new[] { "--aas" }, Kind = DirectiveParameterKind.Flag, Description = "Azure Analysis Services (Entra token)." },
            new() { Name = "--default", Kind = DirectiveParameterKind.Flag, Description = "Make this the default cube." },
            new() { Name = "--password", Aliases = new[] { "-p" }, Kind = DirectiveParameterKind.Forbidden,
                ForbiddenMessage = "Passwords must not be placed in notebook cells. Use --secret <env-var> " +
                    "(resolved from an environment variable), Integrated auth, or Entra instead." },
        },
    };

    /// <summary>The <c>--secret</c> reference on a connect line, or null. Lets a caller put the
    /// password in the store under the right key before the line is parsed.</summary>
    public static string SecretRefOf(string line) =>
        DirectiveParser.FindValue(line, "--secret", "--secret-ref");

    /// <summary>
    /// Parses a <c>#!dax-connect</c> line. Flags: <c>--name</c>, <c>--server</c>,
    /// <c>--database</c>, <c>--user</c>, <c>--secret</c> (an env-var name / ref),
    /// <c>--auth</c> (integrated|sql|aad), <c>--fabric --workspace W --model M</c>,
    /// <c>--azure-as</c>, <c>--connection-string</c>, <c>--default</c>. A committed
    /// <c>--password</c> is rejected.
    /// </summary>
    /// <param name="line">The full <c>#!dax-connect</c> line, selector included.</param>
    /// <param name="secrets">Resolves a <c>--secret</c> reference. Defaults to a fresh store,
    /// which reads the OS credential manager and the <c>CLRKERNEL_SECRET_*</c> environment
    /// variables — the same places a SQL connection's password comes from.</param>
    public static DaxConnectDirective ParseConnect(string line, SecretStore secrets = null) {
        var args = DirectiveParser.Parse(ConnectDefinition, line);
        var name = args.Get("--name");
        var server = args.Get("--server");
        var database = args.Get("--database");
        var user = args.Get("--user");
        var secret = args.Get("--secret");
        var auth = args.Get("--auth")?.ToLowerInvariant();
        var workspace = args.Get("--workspace");
        var model = args.Get("--model");
        var connectionString = args.Get("--connection-string");
        var fabric = args.Has("--fabric");
        var integrated = args.Has("--integrated");
        var azureAs = args.Has("--azure-as");

        SsasConnectionSpec spec;
        if (!string.IsNullOrWhiteSpace(connectionString)) {
            spec = AnalysisServices.FromConnectionString(connectionString).Spec;
        } else if (fabric || (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(model))) {
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(model)) {
                throw new FormatException("#!dax-connect --fabric requires --workspace and --model.");
            }
            // --integrated hands the XMLA endpoint the signed-in Windows identity instead of a
            // token fetched here. On an Entra-joined machine that is frequently the only thing the
            // tenant accepts, since a token this process obtains comes from a generic developer
            // application rather than one the tenant has approved.
            spec = integrated
                ? AnalysisServices.ConnectFabricIntegrated(workspace, model).Spec
                : AnalysisServices.ConnectFabric(workspace, model).Spec;
        } else if (integrated && !string.IsNullOrWhiteSpace(server)) {
            spec = AnalysisServices.Connect(server, database).Spec;
        } else if (azureAs || auth == "aad" || auth == "entra") {
            RequireServer(server);
            spec = integrated
                ? AnalysisServices.Connect(server, database).Spec
                : AnalysisServices.ConnectAzureAnalysisServices(server, database).Spec;
        } else if (!string.IsNullOrWhiteSpace(user) || auth == "sql" || auth == "user") {
            RequireServer(server);
            var password = string.IsNullOrWhiteSpace(secret)
                ? null
                : (secrets ?? new SecretStore()).Resolve(secret);
            spec = AnalysisServices.Connect(server, database, user, password).Spec;
            // Keep the reference, not just the resolved password, so this cube can be written to a
            // connections.json without the password going with it.
            spec.SecretRef = secret;
        } else {
            RequireServer(server);
            spec = AnalysisServices.Connect(server, database).Spec;
        }

        return new DaxConnectDirective(name, spec, args.Has("--default"));
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
    public static string SelectorConnection(string selectorLine) =>
        DirectiveParser.FindValue(selectorLine, "--connections", "--connection", "--cube", "-c");

    private static void RequireServer(string server) {
        if (string.IsNullOrWhiteSpace(server)) {
            throw new FormatException("#!dax-connect requires --server (or --connection-string / --fabric).");
        }
    }
}
