using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.DataEngineering;

namespace ClrKernel.Language.Sql;

public sealed class DeployOptions {
    public string Path { get; set; }
    public bool Recurse { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Disable the CREATE → CREATE OR ALTER rewrite (deploy files as-is).</summary>
    public bool NoAlter { get; set; }
}

/// <summary>
/// Turns a folder of <c>.sql</c> definition files into the provider-agnostic
/// <see cref="DeployFile"/> list that <see cref="DeployRunner"/> executes.
/// <para>
/// This is the T-SQL-specific half of deployment: finding <c>.sql</c> files, ordering them by
/// filename (so numeric prefixes like <c>01_tables.sql</c> work), splitting on <c>GO</c>, and
/// rewriting <c>CREATE</c> to <c>CREATE OR ALTER</c> so re-running is safe. The multi-pass retry
/// that resolves cross-file dependencies is generic and lives in <see cref="DeployRunner"/>.
/// </para>
/// </summary>
public static class SqlDeployPlan {
    /// <summary>Reads and prepares the .sql files under the folder (no execution).</summary>
    public static IReadOnlyList<DeployFile> Plan(DeployOptions options) {
        if (options == null || string.IsNullOrWhiteSpace(options.Path)) {
            throw new ArgumentException("Deploy requires a --path folder.");
        }
        if (!Directory.Exists(options.Path)) {
            throw new DirectoryNotFoundException($"Deploy path not found: {options.Path}");
        }
        var search = options.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(options.Path, "*.sql", search)
            .OrderBy(p => RelativeName(options.Path, p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DeployFile>();
        foreach (var path in files) {
            var text = File.ReadAllText(path);
            var batches = GoBatchSplitter.Split(text)
                .Select(b => options.NoAlter ? b : CreateOrAlter.Transform(b))
                .ToList();
            if (batches.Count > 0) {
                result.Add(new DeployFile(path, RelativeName(options.Path, path), batches));
            }
        }
        return result;
    }

    private static string RelativeName(string root, string path) {
        var rel = path.Substring(root.Length).TrimStart('/', '\\');
        return rel.Replace('\\', '/');
    }
}
