using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.DataEngineering;

public enum DeployState { Planned, Deployed, Failed }

/// <summary>One file's units of work, ready to deploy.</summary>
/// <remarks>What a "batch" is belongs to the provider — for SQL Server it is a <c>GO</c>-separated
/// T-SQL batch. This type only knows they run in order and any of them may throw.</remarks>
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
/// Deploys planned files idempotently, retrying failures across passes: a file that fails because
/// something it references doesn't exist yet is retried in a later pass, until a pass makes no
/// progress. That resolves cross-file dependencies without parsing references.
/// <para>
/// Provider-agnostic by construction — the batch executor is injected, so this knows nothing about
/// SQL, connections or files. Reading a folder and splitting it into batches is the provider's job
/// (for SQL Server, <c>ClrKernel.Language.Sql.SqlDeployPlan</c>).
/// </para>
/// </summary>
public static class DeployRunner {
    /// <summary>
    /// Deploys the planned files, retrying failures across passes. The executor runs a single
    /// batch and throws on error.
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
}
