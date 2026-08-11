using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;

/// <summary>
/// Pipeline orchestration and definition deployment for a session: registering
/// <c>-- step</c> cells, running the dependency DAG in parallel (<c>#!sql-run</c>),
/// and deploying a folder of definitions idempotently (<c>#!sql-deploy</c>).
/// </summary>
public sealed partial class SqlSession {
    private readonly Pipeline _pipeline = new Pipeline();

    /// <summary>The session's registered pipeline steps.</summary>
    public Pipeline Pipeline => _pipeline;

    // Registers (or replaces) a pipeline step from a -- step cell.
    private DisplayData RegisterStep(SqlCellRequest request) {
        var step = new PipelineStep(request.StepName, request.Sql, request.ConnectionName, request.Needs);
        _pipeline.Register(step);
        var needs = request.Needs != null && request.Needs.Count > 0
            ? " (needs: " + string.Join(", ", request.Needs) + ")"
            : "";
        return new DisplayData($"✓ Registered step '{request.StepName}'{needs}. Run #!sql-run to execute the pipeline.");
    }

    /// <summary>Runs the pipeline DAG (all steps, or a selection) in parallel.</summary>
    public DisplayData ExecuteRun(string directiveLine) {
        var d = SqlOrchestrationDirectives.ParseRun(directiveLine);
        var steps = d.Select == null
            ? _pipeline.All.ToList()
            : _pipeline.Select(d.Select).ToList();

        if (steps.Count == 0) {
            return new DisplayData(
                "No pipeline steps are registered. Run the -- step cells first, then #!sql-run.");
        }

        DisplayedValue view = null;
        void Board(IReadOnlyList<StepStatus> statuses) {
            var html = PipelineBoard.Render(statuses);
            if (view == null) {
                view = html.DisplayAs("text/html");
            } else {
                view.Update(html);
            }
        }

        var runner = new PipelineRunner(d.MaxParallel, Board);
        var result = runner.RunAsync(steps, ExecuteStep).GetAwaiter().GetResult();

        var done = result.Steps.Count(s => s.State == StepState.Done);
        var failed = result.Steps.Where(s => s.State == StepState.Failed).ToList();
        var skipped = result.Steps.Count(s => s.State == StepState.Skipped);
        if (!result.Success) {
            throw new SqlCellException(
                $"Pipeline failed: {failed.Count} failed, {skipped} skipped, {done} done. " +
                "First error: " + (failed.FirstOrDefault()?.Outcome?.Error ?? "unknown"));
        }
        return new DisplayData($"Pipeline complete: {done} step(s) succeeded.");
    }

    // Executes one pipeline step: a #!sql-merge / #!sql-bulk magic, or plain SQL.
    private StepOutcome ExecuteStep(PipelineStep step) {
        var firstCode = FirstCodeLine(step.Body);
        var stopwatch = Stopwatch.StartNew();
        if (firstCode.StartsWith("#!sql-merge", StringComparison.OrdinalIgnoreCase)) {
            var dd = ExecuteMerge(firstCode);
            return StepOutcome.Ok((string)dd.Data["text/plain"], stopwatch.ElapsedMilliseconds);
        }
        if (firstCode.StartsWith("#!sql-bulk", StringComparison.OrdinalIgnoreCase)) {
            var dd = ExecuteBulk(firstCode);
            return StepOutcome.Ok((string)dd.Data["text/plain"], stopwatch.ElapsedMilliseconds);
        }
        var rows = RunPlain(step.Connection, step.Body);
        var message = rows >= 0 ? $"{rows:N0} row(s) affected" : "OK";
        return StepOutcome.Ok(message, stopwatch.ElapsedMilliseconds);
    }

    // Runs a plain-SQL step body on a connection and returns rows affected.
    private long RunPlain(string connectionName, string sql) {
        using var connection = OpenConnection(connectionName);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.NextResult()) { }
        return reader.RecordsAffected;
    }

    private static string FirstCodeLine(string body) {
        foreach (var raw in (body ?? string.Empty).Replace("\r\n", "\n").Split('\n')) {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("--")) {
                continue;
            }
            return t;
        }
        return string.Empty;
    }

    // --- Definition deployment ---------------------------------------------

    /// <summary>Deploys a folder of .sql definitions idempotently.</summary>
    public DeployResult Deploy(string connectionName, DeployOptions options, Action<IReadOnlyList<DeployFileResult>> onProgress = null) {
        var files = DeployRunner.Plan(options);
        if (options.DryRun) {
            return DeployRunner.DryRun(files);
        }
        using var connection = OpenConnection(connectionName);
        void Execute(string batch) {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }
        return DeployRunner.Run(files, Execute, onProgress);
    }

    /// <summary>Runs a <c>#!sql-deploy</c> magic and returns a summary board.</summary>
    public DisplayData ExecuteDeploy(string directiveLine) {
        var d = SqlOrchestrationDirectives.ParseDeploy(directiveLine);

        DisplayedValue view = null;
        void Board(IReadOnlyList<DeployFileResult> files) {
            var html = DeployBoard.Render(files, d.Options.DryRun);
            if (view == null) {
                view = html.DisplayAs("text/html");
            } else {
                view.Update(html);
            }
        }

        DeployResult result;
        if (d.Options.DryRun) {
            result = Deploy(d.Connection, d.Options);
            Board(result.Files);
        } else {
            result = Deploy(d.Connection, d.Options, Board);
        }

        if (!result.Success) {
            var firstError = result.Files.FirstOrDefault(f => f.State == DeployState.Failed)?.Error ?? "unknown";
            throw new SqlCellException($"Deploy failed: {result.Failed} file(s) could not be applied. First error: {firstError}");
        }
        var verb = d.Options.DryRun ? "planned" : "deployed";
        return new DisplayData($"{result.Deployed} definition file(s) {verb}.");
    }
}
