using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;

public sealed class DeployOptions {
    public string Path { get; set; }
    public bool Recurse { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Disable the CREATE → CREATE OR ALTER rewrite (deploy files as-is).</summary>
    public bool NoAlter { get; set; }
}

public enum DeployState { Planned, Deployed, Failed }

/// <summary>One .sql file's batches, ready to deploy.</summary>
public sealed class DeployFile {
    public DeployFile(string path, string name, IReadOnlyList<string> batches) {
        Path = path;
        Name = name;
        Batches = batches;
    }
    public string Path { get; }
    public string Name { get; }
    public IReadOnlyList<string> Batches { get; }
}

public sealed class DeployFileResult {
    public string Name { get; set; }
    public DeployState State { get; set; }
    public int Batches { get; set; }
    public int Pass { get; set; }
    public string Error { get; set; }
}

public sealed class DeployResult {
    public DeployResult(IReadOnlyList<DeployFileResult> files) {
        Files = files;
    }
    public IReadOnlyList<DeployFileResult> Files { get; }
    public bool Success { get; set; }
    public int Deployed => Files.Count(f => f.State == DeployState.Deployed);
    public int Failed => Files.Count(f => f.State == DeployState.Failed);
}

/// <summary>
/// Deploys a folder of .sql definition files idempotently. Files run in
/// filename order (so numeric prefixes like <c>01_tables.sql</c> work), and
/// files that fail because a referenced object isn't there yet are retried in
/// later passes until no more progress is made — resolving cross-file
/// dependencies without parsing references. With <c>CREATE OR ALTER</c>
/// (default), re-running is safe. The batch executor is injected so planning and
/// the multi-pass logic are unit-tested without a database.
/// </summary>
public static class DeployRunner {
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

    /// <summary>
    /// Deploys the planned files, retrying failures across passes. The executor
    /// runs a single batch and throws on error.
    /// </summary>
    public static DeployResult Run(
        IReadOnlyList<DeployFile> files,
        Action<string> executeBatch,
        Action<IReadOnlyList<DeployFileResult>> onProgress = null) {
        var results = files.ToDictionary(f => f.Name,
            f => new DeployFileResult { Name = f.Name, State = DeployState.Planned, Batches = f.Batches.Count },
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DeployFileResult> Snapshot() => files.Select(f => results[f.Name]).ToList();
        onProgress?.Invoke(Snapshot());

        var remaining = files.ToList();
        var pass = 0;
        while (remaining.Count > 0) {
            pass++;
            var progressed = false;
            foreach (var file in remaining.ToList()) {
                try {
                    foreach (var batch in file.Batches) {
                        executeBatch(batch);
                    }
                    results[file.Name].State = DeployState.Deployed;
                    results[file.Name].Pass = pass;
                    results[file.Name].Error = null;
                    remaining.Remove(file);
                    progressed = true;
                    onProgress?.Invoke(Snapshot());
                } catch (Exception e) {
                    results[file.Name].Error = e.Message;
                }
            }
            if (!progressed) {
                break; // remaining files can't be resolved (real error, not ordering)
            }
        }

        foreach (var file in remaining) {
            results[file.Name].State = DeployState.Failed;
        }
        onProgress?.Invoke(Snapshot());

        var result = new DeployResult(Snapshot());
        result.Success = result.Files.All(f => f.State == DeployState.Deployed);
        return result;
    }

    /// <summary>Marks every planned file as Planned (for --dry-run output).</summary>
    public static DeployResult DryRun(IReadOnlyList<DeployFile> files) {
        var list = files.Select(f => new DeployFileResult {
            Name = f.Name,
            State = DeployState.Planned,
            Batches = f.Batches.Count,
        }).ToList();
        return new DeployResult(list) { Success = true };
    }

    private static string RelativeName(string root, string path) {
        var rel = path.Substring(root.Length).TrimStart('/', '\\');
        return rel.Replace('\\', '/');
    }
}
