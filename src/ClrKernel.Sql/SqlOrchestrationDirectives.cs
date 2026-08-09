using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Sql.Deploy;

namespace ClrKernel.Sql;

/// <summary>A parsed <c>#!sql-run</c> magic.</summary>
public sealed class RunDirective {
    /// <summary>Step names to run (with their upstream deps), or null for all.</summary>
    public IReadOnlyList<string> Select { get; set; }
    public int MaxParallel { get; set; } = 4;
}

/// <summary>A parsed <c>#!sql-deploy</c> magic.</summary>
public sealed class DeployDirective {
    public string Connection { get; set; }
    public DeployOptions Options { get; } = new DeployOptions();
}

/// <summary>Parses the <c>#!sql-run</c> and <c>#!sql-deploy</c> magics.</summary>
public static class SqlOrchestrationDirectives {
    public static RunDirective ParseRun(string line) {
        var tokens = Tokenize(line, "#!sql-run");
        var d = new RunDirective();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--select":
                case "-s":
                    d.Select = Next().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    break;
                case "--max-parallel":
                case "-p":
                    d.MaxParallel = int.TryParse(Next(), out var n) ? n : throw new FormatException("--max-parallel expects a number.");
                    break;
                default: throw new FormatException($"Unknown #!sql-run flag '{t}'.");
            }
        }
        return d;
    }

    public static DeployDirective ParseDeploy(string line) {
        var tokens = Tokenize(line, "#!sql-deploy");
        var d = new DeployDirective();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--connection": case "-c": d.Connection = Next(); break;
                case "--path": case "--folder": d.Options.Path = Next(); break;
                case "--recurse": case "-r": d.Options.Recurse = true; break;
                case "--dry-run": d.Options.DryRun = true; break;
                case "--no-alter": d.Options.NoAlter = true; break;
                default: throw new FormatException($"Unknown #!sql-deploy flag '{t}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(d.Options.Path)) {
            throw new FormatException("#!sql-deploy requires --path <folder>.");
        }
        return d;
    }

    private static List<string> Tokenize(string line, string selector) {
        var trimmed = (line ?? string.Empty).TrimStart();
        if (trimmed.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed.Substring(selector.Length);
        }
        return SqlDirectives.Tokenize(trimmed);
    }
}
