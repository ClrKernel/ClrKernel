using System;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.Kusto;

namespace ClrKernel.Language.Kql;

/// <summary>A parsed <c>#!kql-connect</c>: a named database plus whether it is the default.</summary>
public sealed class KqlConnectDirective {
    public KqlConnectDirective(string name, KustoConnectionSpec spec, bool isDefault) {
        Name = name;
        Spec = spec;
        IsDefault = isDefault;
    }
    public string Name { get; }
    public KustoConnectionSpec Spec { get; }
    public bool IsDefault { get; }
}

/// <summary>A parsed <c>#!kql</c> cell: the chosen connection (or null) and the query.</summary>
public sealed class KqlCellRequest {
    public KqlCellRequest(string connectionName, string kql) {
        ConnectionName = connectionName;
        Kql = kql;
    }
    public string ConnectionName { get; }
    public string Kql { get; }
}

/// <summary>Parses the <c>#!kql-connect</c> magic and the per-cell connection selector.</summary>
public static class KqlDirectives {
    public static readonly DirectiveDefinition CellDefinition = new() {
        Selector = "#!kql",
        Description = "Runs the cell as KQL against a registered Kusto database.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--connections", Aliases = new[] { "--connection", "-c" }, ValueRole = "connection", Description = "Database to query (the default when omitted)." },
        },
    };

    public static readonly DirectiveDefinition ConnectDefinition = new() {
        Selector = "#!kql-connect",
        Description = "Registers a named Kusto database (Azure Data Explorer, Fabric Eventhouse, Log Analytics) for #!kql cells.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true, Description = "Connection name." },
            new() { Name = "--cluster", Aliases = new[] { "--server", "--url", "-s" }, Required = true, Description = "Cluster URL, or a Fabric Eventhouse query URI." },
            new() { Name = "--database", Aliases = new[] { "-d" }, Required = true, Description = "Database." },
            new() { Name = "--auth", Aliases = new[] { "-a" }, EnumValues = new[] { "entra", "interactive", "clientsecret" }, ValueDetail = "auth mode",
                Description = "entra (default chain, then a browser), interactive (always a browser), clientsecret (a service principal)." },
            new() { Name = "--tenant", Aliases = new[] { "--tenant-id" }, Description = "Tenant id (service principal)." },
            new() { Name = "--client-id", Aliases = new[] { "--app-id" }, Description = "Client / application id (service principal)." },
            new() { Name = "--secret", Aliases = new[] { "--secret-ref" }, Description = "Secret reference for the client secret." },
            new() { Name = "--default", Kind = DirectiveParameterKind.Flag, Description = "Make this the default database." },
            new() { Name = "--client-secret", Aliases = new[] { "--password", "-p" }, Kind = DirectiveParameterKind.Forbidden,
                ForbiddenMessage = "Secrets must not be placed in notebook cells. Use --secret <reference>, resolved from the secret store or CLRKERNEL_SECRET_*." },
        },
    };

    public static System.Collections.Generic.IReadOnlyList<DirectiveDefinition> AllDefinitions { get; } = new[] {
        CellDefinition, ConnectDefinition,
    };

    /// <summary>The <c>--secret</c> reference on a connect line, or null.</summary>
    public static string SecretRefOf(string line) => DirectiveParser.FindValue(line, "--secret", "--secret-ref");

    /// <summary>Parses a <c>#!kql-connect</c> line into a secret-free spec; a <c>--secret</c> reference is resolved through <paramref name="secrets"/>.</summary>
    public static KqlConnectDirective ParseConnect(string line, SecretStore secrets = null) {
        var args = DirectiveParser.Parse(ConnectDefinition, line);
        var auth = (args.Get("--auth") ?? "entra").ToLowerInvariant();
        var spec = new KustoConnectionSpec {
            Cluster = args.Get("--cluster"),
            Database = args.Get("--database"),
            TenantId = args.Get("--tenant"),
            ClientId = args.Get("--client-id"),
            SecretRef = args.Get("--secret"),
        };
        var servicePrincipal = auth == "clientsecret" || !string.IsNullOrWhiteSpace(spec.ClientId) || !string.IsNullOrWhiteSpace(spec.SecretRef);
        if (servicePrincipal) {
            if (string.IsNullOrWhiteSpace(spec.TenantId) || string.IsNullOrWhiteSpace(spec.ClientId) || string.IsNullOrWhiteSpace(spec.SecretRef)) {
                throw new FormatException("#!kql-connect with a service principal needs --tenant, --client-id and --secret.");
            }
            spec.Auth = KustoAuthMode.ClientSecret;
            // Resolved now, kept beside the reference: the spec can be written to a
            // connections.json without the secret going with it.
            if ((secrets ?? new SecretStore()).TryResolve(spec.SecretRef, out var secret)) {
                spec.ClientSecret = secret;
            }
        } else {
            spec.Auth = auth == "interactive" ? KustoAuthMode.Interactive : KustoAuthMode.Entra;
        }
        return new KqlConnectDirective(args.Get("--name"), spec, args.Has("--default"));
    }

    /// <summary>
    /// Which database a <c>#!kql</c> cell targets: an inline <c>#!kql --connections name</c>, or a
    /// leading KQL comment <c>// connections name</c>. Null → the default.
    /// </summary>
    public static KqlCellRequest ParseCell(string cellBody) {
        var text = cellBody ?? string.Empty;
        string connection = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n')) {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (trimmed.StartsWith("#!kql", StringComparison.OrdinalIgnoreCase)) {
                connection ??= SelectorConnection(trimmed);
                continue;
            }
            if (trimmed.StartsWith("//")) {
                var rest = trimmed.Substring(2).Trim();
                foreach (var kw in new[] { "connections", "connection" }) {
                    if (rest.StartsWith(kw, StringComparison.OrdinalIgnoreCase) &&
                        (rest.Length == kw.Length || !char.IsLetterOrDigit(rest[kw.Length]))) {
                        var after = rest.Substring(kw.Length).TrimStart(':', '=', ' ', '\t').Trim();
                        connection ??= after.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    }
                }
                continue;
            }
            break; // first real KQL line ends directive scanning
        }
        return new KqlCellRequest(connection, text);
    }

    /// <summary>Reads the connection name from a <c>#!kql --connections name</c> line.</summary>
    public static string SelectorConnection(string selectorLine) =>
        DirectiveParser.FindValue(selectorLine, "--connections", "--connection", "-c");
}
