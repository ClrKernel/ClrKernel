using System.Collections.Generic;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.AnalysisServices;

namespace ClrKernel.Language.Dax;
/// <summary>
/// Holds the named cube connections for a notebook session and runs <c>#!dax</c>
/// cells: a cell's DAX executes against the chosen (or default) cube and the
/// result renders as an interactive grid. Cubes are registered with
/// <c>#!dax-connect</c>.
/// </summary>
public sealed partial class SsasSession {
    private readonly SsasConnectionRegistry _registry = new SsasConnectionRegistry();
    private readonly SecretStore _secrets;

    public SsasSession(SecretStore secrets = null) {
        _secrets = secrets ?? new SecretStore();
    }

    public SsasConnectionRegistry Cubes => _registry;

    /// <summary>The store a cube's password is resolved from — the OS credential manager first,
    /// then <c>CLRKERNEL_SECRET_*</c>. The same store the SQL connections use.</summary>
    public SecretStore Secrets => _secrets;

    /// <summary>Stores a password in the OS credential store; returns the provider used.</summary>
    public string StoreSecret(string secretRef, string secret) => _secrets.Store(secretRef, secret);

    /// <summary>Registers a cube from a <c>#!dax-connect</c> line; returns its name.</summary>
    public string Connect(string directiveLine) {
        var directive = DaxDirectives.ParseConnect(directiveLine, _secrets);
        _registry.Register(directive.Name, directive.Spec, directive.IsDefault);
        return directive.Name;
    }

    /// <summary>Runs a <c>#!dax</c> cell against its cube and returns the result grid.</summary>
    public DisplayData Execute(string cellBody) {
        var request = DaxDirectives.ParseCell(cellBody);
        var spec = _registry.Resolve(request.CubeName);
        var connection = new SsasConnection(spec);
        return connection.Query(StripDirectiveLines(request.Dax));
    }

    // Removes leading #!dax / -- connections / // connections lines so the DAX
    // sent to the server is clean (comments are harmless, but the #!dax line isn't DAX).
    private static string StripDirectiveLines(string body) {
        var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var stillLeading = true;
        foreach (var line in lines) {
            var trimmed = line.Trim();
            if (stillLeading) {
                if (trimmed.Length == 0) {
                    continue;
                }
                if (trimmed.StartsWith("#!dax", System.StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                stillLeading = false;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }
}
