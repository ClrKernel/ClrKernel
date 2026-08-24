using System;
using System.Collections.Generic;

namespace ClrKernel.Jobs;

public enum RunStatus {
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
}

public enum RunTrigger {
    Manual,
    Schedule,
    Dependency,
    Retry,
}

public enum CellStatus {
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>One execution of a job. Artifact/log paths are relative to the data dir.</summary>
public sealed class Run {
    public Guid Id { get; set; }
    /// <summary>The project this ran in. Job names are only unique within one.</summary>
    public string Project { get; set; } = "default";
    /// <summary>test | prod | default — part of every key; test and prod share job names.</summary>
    public string Environment { get; set; } = "default";
    public string JobName { get; set; }
    public string NotebookPath { get; set; }
    public RunStatus Status { get; set; }
    public RunTrigger Trigger { get; set; }
    /// <summary>The upstream run whose success fired this one (chain lineage).</summary>
    public Guid? CausedByRunId { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTime? ScheduledFor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string ErrorSummary { get; set; }
    public string ArtifactPath { get; set; }
    public string LogPath { get; set; }
    /// <summary>The environment's git HEAD when the run started (promotion evidence).</summary>
    public string CommitSha { get; set; }
    /// <summary>Uncommitted changes under the job's files at run start — never promotable.</summary>
    public bool WasDirty { get; set; }
    /// <summary>Ran with ad-hoc parameter overrides — proves nothing about the yaml as written.</summary>
    public bool HadOverrides { get; set; }
}

/// <summary>Live per-cell progress for a run's code cells, in execution order.</summary>
public sealed class RunCell {
    public Guid RunId { get; set; }
    public int CellIndex { get; set; }
    public CellStatus Status { get; set; }
    /// <summary>First non-empty source line, for the step-by-step view.</summary>
    public string SourcePreview { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string ErrorSummary { get; set; }
}

/// <summary>Fan-in freshness bookkeeping: when each job was last triggered.</summary>
public sealed class JobTriggerState {
    public string Project { get; set; } = "default";
    public string Environment { get; set; } = "default";
    public string JobName { get; set; }
    public DateTime LastTriggerAt { get; set; }
}

public sealed class RunQuery {
    /// <summary>null = all projects.</summary>
    public string Project { get; set; }
    /// <summary>null = all environments.</summary>
    public string Environment { get; set; }
    public string JobName { get; set; }
    public RunStatus? Status { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }
}

public sealed class RunStats {
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
}
