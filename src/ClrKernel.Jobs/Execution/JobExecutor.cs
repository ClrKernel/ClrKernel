using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Runner;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace ClrKernel.Jobs;

/// <summary>
/// Executes one run of a job: spawns an isolated <c>clrkernel serve</c> process
/// (cwd = the notebook's directory), walks the notebook cell by cell over JSON-RPC,
/// records live per-cell progress in the store, and writes the executed .ipynb
/// artifact plus a run.log — even when a cell fails (papermill semantics: first
/// failure stops execution, later cells are Skipped and written unexecuted).
/// </summary>
public sealed class JobExecutor {
    // Same marker NotebookRunner uses: a code cell whose first line is `// parameters`.
    private static readonly Regex _parametersMarker =
        new(@"^//\s*parameters\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IRunStore _store;
    private readonly JobsOptions _options;
    private readonly ILogger _logger;

    public JobExecutor(IRunStore store, JobsOptions options, ILogger logger) {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised on every cell state change (run, cell, total code cells) — the CLI/API progress feed.</summary>
    public event Action<Run, RunCell, int> CellProgress;

    public async Task<Run> ExecuteAsync(
        JobDefinition job, RunTrigger trigger, Guid? causedByRunId = null, int attempt = 1,
        Guid? runId = null, CancellationToken cancellationToken = default) {
        var run = new Run {
            Id = runId ?? Guid.NewGuid(),
            JobName = job.Name,
            NotebookPath = job.NotebookRelative,
            Status = RunStatus.Running,
            Trigger = trigger,
            CausedByRunId = causedByRunId,
            Attempt = attempt,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
        };

        var runDir = Path.Combine(_options.ArtifactsDir, job.Name, run.Id.ToString("N"));
        Directory.CreateDirectory(runDir);
        var artifactPath = Path.Combine(runDir, "output.ipynb");
        var logPath = Path.Combine(runDir, "run.log");
        run.ArtifactPath = Path.GetRelativePath(_options.DataDir, artifactPath).Replace('\\', '/');
        run.LogPath = Path.GetRelativePath(_options.DataDir, logPath).Replace('\\', '/');
        // Every run — scheduled, manual, or chained — moves the job's trigger clock,
        // which is what the dependency freshness rule compares successes against.
        await _store.SetLastTriggerAsync(job.Name, DateTime.UtcNow);
        await _store.CreateRunAsync(run);

        using var log = new StreamWriter(logPath) { AutoFlush = true };
        void Log(string line) => log.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {line}");

        // Per-job timeout composed with the caller's cancellation (manual cancel / shutdown).
        using var timeout = new CancellationTokenSource();
        if (job.TimeoutSeconds is { } seconds and > 0) {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try {
            var plan = BuildPlan(job);
            var cells = SeedCells(run.Id, plan);
            await _store.SaveCellsAsync(run.Id, cells);

            await RunCellsAsync(job, run, plan, cells, artifactPath, Log, linked.Token);
        } catch (OperationCanceledException) {
            run.Status = timeout.IsCancellationRequested ? RunStatus.TimedOut : RunStatus.Cancelled;
            run.ErrorSummary = run.Status == RunStatus.TimedOut
                ? $"Timed out after {job.TimeoutSeconds}s."
                : "Cancelled.";
            Log(run.ErrorSummary);
        } catch (Exception e) {
            run.Status = RunStatus.Failed;
            run.ErrorSummary = e.Message;
            _logger.LogError(e, "Run {RunId} of job {Job} failed.", run.Id, job.Name);
            Log($"FAILED: {e}");
        } finally {
            run.FinishedAt = DateTime.UtcNow;
            await _store.UpdateRunAsync(run);
        }
        return run;
    }

    // --- plan -----------------------------------------------------------------

    internal sealed class PlanCell {
        public NotebookCell Cell { get; init; }
        public bool Injected { get; init; }
        /// <summary>Index into the run_cells rows; -1 for markdown cells.</summary>
        public int CodeIndex { get; set; } = -1;
    }

    internal List<PlanCell> BuildPlan(JobDefinition job) {
        var parsed = NotebookDocument.Parse(job.NotebookPath);
        var plan = parsed.Select(c => new PlanCell { Cell = c }).ToList();

        var injected = RenderParametersCell(job.Parameters);
        if (injected != null) {
            var marker = plan.FindIndex(p =>
                p.Cell.Kind == CellKind.Code && _parametersMarker.IsMatch(FirstLine(p.Cell.Source)));
            plan.Insert(marker + 1, new PlanCell {
                Cell = new NotebookCell(CellKind.Code, injected.TrimEnd()),
                Injected = true,
            });
        }

        var codeIndex = 0;
        foreach (var cell in plan.Where(p => p.Cell.Kind == CellKind.Code)) {
            cell.CodeIndex = codeIndex++;
        }
        return plan;
    }

    /// <summary>Renders the papermill injected-parameters cell, or null when there are none.</summary>
    internal static string RenderParametersCell(IReadOnlyDictionary<string, object> parameters) {
        if (parameters is not { Count: > 0 }) {
            return null;
        }
        // Round-trip through YAML so RunnerParameters' full type inference applies
        // (bool/int/long/double/string, nested maps and sequences).
        var yaml = new SerializerBuilder().Build().Serialize(parameters.ToDictionary(kv => kv.Key, kv => kv.Value));
        var runnerParameters = new RunnerParameters();
        runnerParameters.MergeYaml(yaml);
        return runnerParameters.RenderCell();
    }

    internal static List<RunCell> SeedCells(Guid runId, List<PlanCell> plan) =>
        plan.Where(p => p.CodeIndex >= 0)
            .Select(p => new RunCell {
                RunId = runId,
                CellIndex = p.CodeIndex,
                Status = CellStatus.Pending,
                SourcePreview = FirstLine(p.Cell.Source),
            })
            .ToList();

    private static string FirstLine(string source) =>
        source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    // --- execution --------------------------------------------------------------

    private async Task RunCellsAsync(
        JobDefinition job, Run run, List<PlanCell> plan, List<RunCell> cells,
        string artifactPath, Action<string> log, CancellationToken cancellationToken) {
        var clrkernel = ClrKernelLocator.Find(_options.ClrKernelPath);
        using var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = clrkernel,
                Arguments = "serve",
                WorkingDirectory = Path.GetDirectoryName(job.NotebookPath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                log($"kernel: {e.Data}");
            }
        };
        log($"Starting {clrkernel} serve (cwd {process.StartInfo.WorkingDirectory})");
        process.Start();
        process.BeginErrorReadLine();

        try {
            using var client = new KernelClient(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
            await ExecuteCellsAsync(client, run, plan, cells, artifactPath, log, cancellationToken);
        } finally {
            KillIfRunning(process, log);
        }
    }

    /// <summary>
    /// The cell loop, over an already-connected client. Internal seam: tests drive it
    /// with an in-memory duplex stream instead of a real process.
    /// </summary>
    internal async Task ExecuteCellsAsync(
        KernelClient client, Run run, List<PlanCell> plan, List<RunCell> cells,
        string artifactPath, Action<string> log, CancellationToken cancellationToken) {
        // A kernel that never answers initialize would otherwise hang an
        // untimed job forever.
        using var initTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        initTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        InitializeReply info;
        try {
            info = await client.InitializeAsync(initTimeout.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException("The kernel did not answer initialize within 60s.");
        }
        log($"Kernel ready: {info.Name} {info.Version}");

        // Notifications carry the cellId we executed with ("cell-<index>"), so each
        // display lands on its own cell regardless of handler/reply ordering — and a
        // background update arriving after the cell's reply still reaches it.
        var outputsByCell = new Dictionary<int, JsonArray>();
        client.DisplayReceived += notification => {
            if (notification.Data == null || notification.CellId is not { } cellId
                || !cellId.StartsWith("cell-", StringComparison.Ordinal)
                || !int.TryParse(cellId["cell-".Length..], out var codeIndex)) {
                return;
            }
            var data = notification.Data.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            lock (outputsByCell) {
                if (outputsByCell.TryGetValue(codeIndex, out var cellOutputs)) {
                    cellOutputs.Add(IpynbWriter.DisplayDataOutput(data));
                }
            }
            if (notification.Data.TryGetValue("text/plain", out var text)) {
                log(text.ToString());
            }
        };

        var executedCells = new List<JsonObject>();
        var failed = false;
        var executionCount = 0;

        foreach (var planCell in plan) {
            if (planCell.Cell.Kind == CellKind.Markdown) {
                executedCells.Add(IpynbWriter.MarkdownCell(planCell.Cell.Source));
                continue;
            }

            var cell = cells[planCell.CodeIndex];
            if (failed) {
                cell.Status = CellStatus.Skipped;
                await _store.UpdateCellAsync(cell);
                CellProgress?.Invoke(run, cell, cells.Count);
                executedCells.Add(UnexecutedCell(planCell));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            executionCount++;
            cell.Status = CellStatus.Running;
            cell.StartedAt = DateTime.UtcNow;
            await _store.UpdateCellAsync(cell);
            CellProgress?.Invoke(run, cell, cells.Count);
            log($"cell {planCell.CodeIndex + 1}/{cells.Count}: {cell.SourcePreview}");

            var outputs = new JsonArray();
            lock (outputsByCell) {
                outputsByCell[planCell.CodeIndex] = outputs;
            }
            ExecuteReply reply;
            try {
                reply = await client.ExecuteAsync($"cell-{planCell.CodeIndex}", planCell.Cell.Source, cancellationToken);
            } catch (OperationCanceledException) {
                // Timeout or manual cancel mid-cell: record where it stopped, then let
                // ExecuteAsync's handler set the run status.
                cell.Status = CellStatus.Failed;
                cell.ErrorSummary = "Interrupted (timeout or cancel).";
                cell.FinishedAt = DateTime.UtcNow;
                await _store.UpdateCellAsync(cell);
                CellProgress?.Invoke(run, cell, cells.Count);
                throw;
            }

            cell.FinishedAt = DateTime.UtcNow;
            if (reply.Ok) {
                if (reply.Data is { Count: > 0 }) {
                    outputs.Add(IpynbWriter.ExecuteResultOutput(
                        executionCount, reply.Data.ToDictionary(kv => kv.Key, kv => (object)kv.Value)));
                }
                cell.Status = CellStatus.Succeeded;
            } else {
                var error = reply.Error ?? new ExecuteError { Name = "Error", Message = "Cell failed." };
                var traceback = (error.Stack ?? string.Empty)
                    .Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);
                outputs.Add(IpynbWriter.ErrorOutput(error.Name, error.Message, traceback));
                cell.Status = CellStatus.Failed;
                cell.ErrorSummary = $"{error.Name}: {error.Message}";
                run.ErrorSummary = $"cell {planCell.CodeIndex + 1}: {cell.ErrorSummary}";
                log($"cell {planCell.CodeIndex + 1} FAILED: {cell.ErrorSummary}");
                failed = true;
            }
            await _store.UpdateCellAsync(cell);
            CellProgress?.Invoke(run, cell, cells.Count);

            executedCells.Add(IpynbWriter.CodeCell(
                planCell.Cell.Source, executionCount, outputs,
                planCell.Injected ? new[] { "injected-parameters" } : null));
        }

        run.Status = failed ? RunStatus.Failed : RunStatus.Succeeded;
        IpynbWriter.Write(artifactPath, executedCells);
        log($"Run {run.Status}. Artifact: {artifactPath}");

        await client.ShutdownAsync();
    }

    private static JsonObject UnexecutedCell(PlanCell planCell) =>
        IpynbWriter.CodeCell(planCell.Cell.Source, null, new JsonArray(),
            planCell.Injected ? new[] { "injected-parameters" } : null);

    private static void KillIfRunning(Process process, Action<string> log) {
        try {
            if (!process.WaitForExit(2000)) {
                process.Kill(entireProcessTree: true);
                log("Kernel process killed.");
            }
        } catch (Exception) {
            // Already exited between the check and the kill.
        }
    }
}
