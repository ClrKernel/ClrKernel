using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

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
    /// <summary>The declarative shape of <c>#!sql-run</c>.</summary>
    public static readonly DirectiveDefinition RunDefinition = new() {
        Selector = "#!sql-run",
        Description = "Runs the notebook's SQL pipeline steps.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--select", Aliases = new[] { "-s" }, Description = "Step names to run (comma-separated); default all." },
            new() { Name = "--max-parallel", Aliases = new[] { "-p" }, Description = "Maximum parallel steps (default 4)." },
        },
    };

    /// <summary>The declarative shape of <c>#!sql-deploy</c>.</summary>
    public static readonly DirectiveDefinition DeployDefinition = new() {
        Selector = "#!sql-deploy",
        Description = "Deploys .sql files from a folder.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--connection", Aliases = new[] { "-c" }, Description = "Connection name." },
            new() { Name = "--path", Aliases = new[] { "--folder" }, Required = true, RequiredLabel = "--path <folder>", Description = "Folder of .sql files." },
            new() { Name = "--recurse", Aliases = new[] { "-r" }, Kind = DirectiveParameterKind.Flag, Description = "Recurse into subfolders." },
            new() { Name = "--dry-run", Kind = DirectiveParameterKind.Flag, Description = "Report without executing." },
            new() { Name = "--no-alter", Kind = DirectiveParameterKind.Flag, Description = "Never ALTER existing objects." },
        },
    };

    public static RunDirective ParseRun(string line) {
        var args = DirectiveParser.Parse(RunDefinition, line);
        var d = new RunDirective();
        if (args.Has("--select")) {
            d.Select = args.Get("--select").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }
        if (args.Has("--max-parallel")) {
            d.MaxParallel = int.TryParse(args.Get("--max-parallel"), out var n)
                ? n
                : throw new FormatException("--max-parallel expects a number.");
        }
        return d;
    }

    public static DeployDirective ParseDeploy(string line) {
        var args = DirectiveParser.Parse(DeployDefinition, line);
        var d = new DeployDirective { Connection = args.Get("--connection") };
        d.Options.Path = args.Get("--path");
        d.Options.Recurse = args.Has("--recurse");
        d.Options.DryRun = args.Has("--dry-run");
        d.Options.NoAlter = args.Has("--no-alter");
        return d;
    }
}
