using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Studio;

/// <summary>
/// The job dependency DAG: validation (every dependsOn resolves, no cycles) and a
/// dependents lookup for chain triggering. Pure and side-effect-free, same shape as
/// the DataEngineering pipeline graph.
/// </summary>
public sealed class JobGraph {
    private readonly Dictionary<string, JobDefinition> _jobs;
    private readonly Dictionary<string, List<string>> _dependents;

    public JobGraph(IEnumerable<JobDefinition> jobs) {
        _jobs = jobs.ToDictionary(j => j.Name, j => j, StringComparer.OrdinalIgnoreCase);
        _dependents = _jobs.Keys.ToDictionary(n => n, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var job in _jobs.Values) {
            foreach (var dep in job.DependsOn) {
                if (_dependents.TryGetValue(dep, out var list)) {
                    list.Add(job.Name);
                }
            }
        }
    }

    /// <summary>Jobs that list <paramref name="jobName"/> in their dependsOn.</summary>
    public IReadOnlyList<string> DependentsOf(string jobName) =>
        _dependents.TryGetValue(jobName, out var list) ? list : Array.Empty<string>();

    /// <summary>
    /// Returns every graph error: unknown dependency names and cycles (found via
    /// Kahn's algorithm — whatever survives peeling has a cycle through it).
    /// </summary>
    public IReadOnlyList<string> Validate() {
        var errors = new List<string>();
        foreach (var job in _jobs.Values) {
            foreach (var dep in job.DependsOn) {
                if (!_jobs.ContainsKey(dep)) {
                    errors.Add($"Job '{job.Name}' depends on unknown job '{dep}'.");
                }
            }
        }

        var indegree = _jobs.Values.ToDictionary(
            j => j.Name, j => j.DependsOn.Count(_jobs.ContainsKey), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var peeled = 0;
        while (queue.Count > 0) {
            var name = queue.Dequeue();
            peeled++;
            foreach (var dependent in DependentsOf(name)) {
                if (--indegree[dependent] == 0) {
                    queue.Enqueue(dependent);
                }
            }
        }
        if (peeled < _jobs.Count) {
            var cyclic = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(n => n);
            errors.Add($"Dependency cycle involving: {string.Join(", ", cyclic)}.");
        }
        return errors;
    }
}
