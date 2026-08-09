using System.Collections.Generic;

namespace ClrKernel.Sql.Pipeline;
/// <summary>The lifecycle state of a step during a pipeline run.</summary>
public enum StepState {
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
}

/// <summary>
/// One node in a pipeline DAG: a named unit of work with declared upstream
/// dependencies. The <see cref="Body"/> is the SQL (or a #!sql-merge / #!sql-bulk
/// magic) to run; <see cref="Connection"/> is the connection for plain-SQL steps.
/// </summary>
public sealed class PipelineStep {
    public PipelineStep(string name, string body, string connection = null, IEnumerable<string> needs = null) {
        Name = name;
        Body = body ?? string.Empty;
        Connection = connection;
        Needs = needs != null ? new List<string>(needs) : new List<string>();
    }

    public string Name { get; }
    public string Body { get; }
    public string Connection { get; }
    public IReadOnlyList<string> Needs { get; }
}

/// <summary>The result of executing one step.</summary>
public sealed class StepOutcome {
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public string Message { get; set; }
    public string Error { get; set; }

    public static StepOutcome Ok(string message, long elapsedMs) =>
        new StepOutcome { Success = true, Message = message, ElapsedMs = elapsedMs };

    public static StepOutcome Fail(string error, long elapsedMs) =>
        new StepOutcome { Success = false, Error = error, ElapsedMs = elapsedMs };
}

/// <summary>A step plus its live state and outcome (for the status board / result).</summary>
public sealed class StepStatus {
    public StepStatus(PipelineStep step) {
        Step = step;
        State = StepState.Pending;
    }
    public PipelineStep Step { get; }
    public StepState State { get; set; }
    public StepOutcome Outcome { get; set; }
}

/// <summary>The overall result of a pipeline run.</summary>
public sealed class PipelineResult {
    public PipelineResult(IReadOnlyList<StepStatus> steps) {
        Steps = steps;
    }
    public IReadOnlyList<StepStatus> Steps { get; }
    public bool Success { get; set; }
}
