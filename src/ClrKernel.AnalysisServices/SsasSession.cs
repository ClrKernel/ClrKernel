using System.Collections.Generic;
using ClrKernel.Core.Primitives;

namespace ClrKernel.AnalysisServices;
/// <summary>
/// Holds the named cube connections for a notebook session and runs <c>#!dax</c>
/// cells: a cell's DAX executes against the chosen (or default) cube and the
/// result renders as an interactive grid. Cubes are registered with
/// <c>#!dax-connect</c>.
/// </summary>
public sealed class SsasSession {
    private readonly SsasConnectionRegistry _registry = new SsasConnectionRegistry();

    public SsasConnectionRegistry Cubes => _registry;

    /// <summary>Registers a cube from a <c>#!dax-connect</c> line; returns its name.</summary>
    public string Connect(string directiveLine) {
        var directive = DaxDirectives.ParseConnect(directiveLine);
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
