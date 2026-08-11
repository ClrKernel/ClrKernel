using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ClrKernel.Language.Sql;
/// <summary>
/// Executes a set of pipeline steps honoring their dependencies: steps whose
/// dependencies are all complete run concurrently up to a parallelism cap, and
/// a failure skips everything downstream of it while independent branches keep
/// going. The step executor is injected, so the scheduler is unit-tested with
/// no database. Progress is reported after every state change for a live board.
/// </summary>
public sealed class PipelineRunner {
    private readonly int _maxParallel;
    private readonly Action<IReadOnlyList<StepStatus>> _onProgress;

    public PipelineRunner(int maxParallel = 4, Action<IReadOnlyList<StepStatus>> onProgress = null) {
        _maxParallel = Math.Max(1, maxParallel);
        _onProgress = onProgress;
    }

    public async Task<PipelineResult> RunAsync(
        IReadOnlyList<PipelineStep> steps,
        Func<PipelineStep, StepOutcome> execute) {
        // Validate the graph (missing deps / cycles) before doing any work.
        new Pipeline().TopologicalOrder(steps);

        var order = steps.Select((s, i) => (s.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        var status = steps.ToDictionary(s => s.Name, s => new StepStatus(s), StringComparer.OrdinalIgnoreCase);
        var remainingDeps = steps.ToDictionary(s => s.Name,
            s => new HashSet<string>(s.Needs, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var dependents = steps.ToDictionary(s => s.Name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps) {
            foreach (var dep in step.Needs) {
                dependents[dep].Add(step.Name);
            }
        }

        var pending = new HashSet<string>(steps.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, Task<StepOutcome>>();

        IReadOnlyList<StepStatus> Snapshot() =>
            status.Values.OrderBy(s => order[s.Step.Name]).ToList();
        void Render() => _onProgress?.Invoke(Snapshot());

        Render();

        while (pending.Count > 0 || running.Count > 0) {
            foreach (var name in pending.Where(n => status[n].State == StepState.Pending && remainingDeps[n].Count == 0)
                         .OrderBy(n => order[n]).ToList()) {
                if (running.Count >= _maxParallel) {
                    break;
                }
                status[name].State = StepState.Running;
                Render();
                var step = status[name].Step;
                running[name] = Task.Run(() => SafeExecute(execute, step));
            }

            if (running.Count == 0) {
                break; // nothing runnable (remaining are blocked by skips)
            }

            var finished = await Task.WhenAny(running.Values).ConfigureAwait(false);
            var doneName = running.First(kv => kv.Value == finished).Key;
            running.Remove(doneName);
            pending.Remove(doneName);

            var outcome = await finished.ConfigureAwait(false);
            status[doneName].Outcome = outcome;
            status[doneName].State = outcome.Success ? StepState.Done : StepState.Failed;
            Render();

            if (outcome.Success) {
                foreach (var dep in dependents[doneName]) {
                    remainingDeps[dep].Remove(doneName);
                }
            } else {
                SkipDependents(doneName, dependents, status, pending);
                Render();
            }
        }

        // Anything still pending was blocked by a failure upstream.
        foreach (var name in pending.ToList()) {
            status[name].State = StepState.Skipped;
        }
        Render();

        var result = new PipelineResult(Snapshot());
        result.Success = result.Steps.All(s => s.State == StepState.Done);
        return result;
    }

    private static void SkipDependents(
        string failed,
        IReadOnlyDictionary<string, List<string>> dependents,
        IReadOnlyDictionary<string, StepStatus> status,
        HashSet<string> pending) {
        var queue = new Queue<string>(dependents[failed]);
        while (queue.Count > 0) {
            var name = queue.Dequeue();
            if (pending.Remove(name)) {
                status[name].State = StepState.Skipped;
                foreach (var next in dependents[name]) {
                    queue.Enqueue(next);
                }
            }
        }
    }

    private static StepOutcome SafeExecute(Func<PipelineStep, StepOutcome> execute, PipelineStep step) {
        var stopwatch = Stopwatch.StartNew();
        try {
            var outcome = execute(step);
            if (outcome != null && outcome.ElapsedMs == 0) {
                outcome.ElapsedMs = stopwatch.ElapsedMilliseconds;
            }
            return outcome ?? StepOutcome.Ok("done", stopwatch.ElapsedMilliseconds);
        } catch (Exception e) {
            return StepOutcome.Fail(e.Message, stopwatch.ElapsedMilliseconds);
        }
    }
}
