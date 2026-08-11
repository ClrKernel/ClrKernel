using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.DataEngineering;
/// <summary>Thrown when the step graph is invalid (missing dep or a cycle).</summary>
public sealed class PipelineGraphException : Exception {
    public PipelineGraphException(string message) : base(message) { }
}

/// <summary>
/// The registered pipeline steps for a session and the DAG operations over
/// them: validation (every <c>-- needs</c> resolves, no cycles), transitive
/// selection, and a dependency-respecting topological order. Pure and
/// side-effect-free so the graph logic is unit-tested without a database.
/// </summary>
public sealed class Pipeline {
    private readonly Dictionary<string, PipelineStep> _steps =
        new Dictionary<string, PipelineStep>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers or replaces a step by name.</summary>
    public void Register(PipelineStep step) {
        if (step == null) {
            throw new ArgumentNullException(nameof(step));
        }
        if (string.IsNullOrWhiteSpace(step.Name)) {
            throw new ArgumentException("A pipeline step needs a name.", nameof(step));
        }
        _steps[step.Name] = step;
    }

    public bool Remove(string name) => _steps.Remove(name);
    public void Clear() => _steps.Clear();
    public bool TryGet(string name, out PipelineStep step) => _steps.TryGetValue(name, out step);
    public IReadOnlyCollection<PipelineStep> All => _steps.Values.ToList();
    public int Count => _steps.Count;

    /// <summary>Validates that every dependency exists and there are no cycles.</summary>
    public void Validate() => TopologicalOrder(_steps.Values.ToList());

    /// <summary>
    /// Expands a set of step names to include all transitive dependencies, so
    /// running a selection also runs what it needs. Unknown names throw.
    /// </summary>
    public IReadOnlyList<PipelineStep> Select(IEnumerable<string> names) {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var n in names) {
            queue.Enqueue(n);
        }
        while (queue.Count > 0) {
            var name = queue.Dequeue();
            if (!_steps.TryGetValue(name, out var step)) {
                throw new PipelineGraphException($"No pipeline step named '{name}'. " +
                    (_steps.Count == 0 ? "Run the -- step cells first to register them." :
                        $"Known steps: {string.Join(", ", _steps.Keys)}."));
            }
            if (wanted.Add(name)) {
                foreach (var dep in step.Needs) {
                    queue.Enqueue(dep);
                }
            }
        }
        return _steps.Values.Where(s => wanted.Contains(s.Name)).ToList();
    }

    /// <summary>
    /// Returns the given steps in an order where every step comes after its
    /// dependencies (Kahn's algorithm). Throws on a missing dependency or a
    /// cycle, naming the steps involved.
    /// </summary>
    public IReadOnlyList<PipelineStep> TopologicalOrder(IReadOnlyList<PipelineStep> steps) {
        var byName = steps.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var indegree = steps.ToDictionary(s => s.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = steps.ToDictionary(s => s.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps) {
            foreach (var dep in step.Needs) {
                if (!byName.ContainsKey(dep)) {
                    throw new PipelineGraphException(
                        $"Step '{step.Name}' needs '{dep}', which is not a registered step in this run.");
                }
                indegree[step.Name]++;
                dependents[dep].Add(step.Name);
            }
        }

        var ready = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        var ordered = new List<PipelineStep>();
        while (ready.Count > 0) {
            var name = ready.Dequeue();
            ordered.Add(byName[name]);
            foreach (var next in dependents[name]) {
                if (--indegree[next] == 0) {
                    ready.Enqueue(next);
                }
            }
        }

        if (ordered.Count != steps.Count) {
            var cyclic = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new PipelineGraphException(
                "The pipeline has a dependency cycle involving: " + string.Join(", ", cyclic) + ".");
        }
        return ordered;
    }
}
