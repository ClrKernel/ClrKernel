using System;
using System.Collections.Generic;

namespace ClrKernel.Studio;

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
    /// <summary>Who pressed run. Null for a scheduled run — nobody did — and null
    /// for any manual run recorded before this column existed; the two are not
    /// distinguishable, so read it beside <see cref="Trigger"/> or not at all.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Denormalised beside the id: the account may be gone by the time anyone asks.</summary>
    public string ActorName { get; set; }
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

/// <summary>
/// Somebody driving a notebook by hand in test or prod.
/// <para>
/// Its own table rather than a row in <c>runs</c>, because it is not a job run and
/// nothing should ever mistake it for one: promotability asks for the latest run of
/// a named job, and an audit entry that could answer that question is a hole in the
/// gate. This records who did what; run history records what the schedule did.
/// </para>
/// </summary>
public sealed class ManualRun {
    public Guid Id { get; set; }
    public string Project { get; set; } = "default";
    /// <summary>test | prod. Running by hand anywhere else is not audited — or allowed.</summary>
    public string Environment { get; set; }
    public string NotebookPath { get; set; }
    public Guid ActorId { get; set; }
    /// <summary>Kept beside the id: the account may be gone by the time anyone asks.</summary>
    public string ActorName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Which cells, in order, as the editor identifies them.</summary>
    public string Cells { get; set; }
    public int CellCount { get; set; }
    /// <summary>Parameters overridden for this execution only, as JSON, or null.</summary>
    public string Overrides { get; set; }
    /// <summary>Running | Succeeded | Failed.</summary>
    public string Outcome { get; set; } = "Running";
    public string ErrorSummary { get; set; }
}

public sealed class ManualRunQuery {
    public string Project { get; set; }
    public string Environment { get; set; }
    public string NotebookPath { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>
/// One statement somebody ran: who, when, which connection, and what they sent.
/// <para>
/// It answers two questions that must not be confused. For a <b>shared</b>
/// connection it is an audit — "who ran that against production?" — and a server
/// admin can read everybody's. For a <b>private</b> one it is that person's own
/// history of their own work, and <em>only they ever see it</em>: not an admin, not
/// anybody. Recording it is what makes a personal history worth having; showing it
/// to somebody else would be the surveillance an earlier version of this avoided by
/// not recording it at all.
/// </para>
/// <para>
/// The rule lives in the store rather than in the routes — see
/// <see cref="QueryAuditQuery"/> — so a new route cannot forget it.
/// </para>
/// </summary>
/// <summary>
/// One promotion: what went to production, who sent it, and what it switched off.
/// <para>
/// Git already records that the files changed, and is the authority on their
/// contents. What it cannot answer is the operational question — who promoted
/// this, which green runs were the evidence, and which schedules stopped running
/// as a result — because a commit deleting a yaml looks exactly like a commit
/// deleting a yaml. That last one is why this exists: an unschedule is the change
/// people notice weeks later, when something did not run.
/// </para>
/// </summary>
public sealed class PromotionAudit {
    public Guid Id { get; set; }
    public string Project { get; set; } = "default";
    /// <summary>The files promoted, '/'-separated and newline-joined.</summary>
    public string Paths { get; set; }
    public Guid ActorId { get; set; }
    /// <summary>Denormalised: the record has to survive the account being removed.</summary>
    public string ActorName { get; set; }
    public DateTime PromotedAt { get; set; }
    /// <summary>True when this removed something from prod rather than updating it.</summary>
    public bool IsDeletion { get; set; }
    /// <summary>The prod commit this produced, or null when nothing changed.</summary>
    public string CommitSha { get; set; }
    /// <summary>Job names whose schedule this stopped, newline-joined. Empty for most.</summary>
    public string Unscheduled { get; set; }
    /// <summary>The run ids that served as evidence, newline-joined.</summary>
    public string EvidenceRuns { get; set; }
}

/// <summary>What to read back out of the promotion log.</summary>
public sealed class PromotionAuditQuery {
    /// <summary>null = every project the caller can see; the route filters.</summary>
    public string Project { get; set; }
    /// <summary>Only promotions that switched a schedule off.</summary>
    public bool UnschedulesOnly { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed class QueryAudit {
    public Guid Id { get; set; }
    public string ConnectionId { get; set; }

    /// <summary>Kept beside the id: the connection may be renamed or removed by the
    /// time anyone asks.</summary>
    public string ConnectionName { get; set; }

    public Guid ActorId { get; set; }
    public string ActorName { get; set; }
    public DateTime StartedAt { get; set; }
    public double DurationMs { get; set; }

    /// <summary>What was sent, verbatim. A truncated statement is not evidence.</summary>
    public string Statement { get; set; }

    /// <summary>Whether the least-privilege login was used — that is, whether this ran
    /// as the read-only credential or as the connection's own.</summary>
    public bool LeastPrivilege { get; set; }

    /// <summary>Succeeded | Failed | Cancelled.</summary>
    public string Outcome { get; set; }

    public int RowsAffected { get; set; }
    public string ErrorSummary { get; set; }

    /// <summary><c>shared</c> | <c>private</c> — which of the two things this row is,
    /// and therefore who may read it.</summary>
    public string Scope { get; set; }
}

/// <summary>
/// Who is asking, and about what. The reader is named rather than the rows wanted,
/// because the filtering is not the caller's to decide: a row about a private
/// connection belongs to its actor alone, and a store that took "give me
/// everything" would let one forgetful route hand it over.
/// </summary>
public sealed class QueryAuditQuery {
    /// <summary>null = every connection this reader may see rows about.</summary>
    public string ConnectionId { get; set; }

    /// <summary>Whose view this is. Rows about private connections are only ever
    /// theirs.</summary>
    public Guid ViewerId { get; set; }

    /// <summary>A server admin reads everybody's rows about <em>shared</em>
    /// connections. Never anybody's private ones.</summary>
    public bool ViewerIsAdmin { get; set; }

    public int Limit { get; set; } = 50;
}

/// <summary>
/// A query somebody kept. Scoped the way connections are — shared ones a server
/// admin manages and everybody can open, private ones invisible to everyone else —
/// because the two are used together and a different rule for each would be a rule
/// nobody could remember.
/// </summary>
public sealed class SavedQuery {
    public Guid Id { get; set; }
    public string Name { get; set; }

    /// <summary><c>shared</c> | <c>private</c>.</summary>
    public string Scope { get; set; }

    /// <summary>Set for a private query, null for a shared one.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The connection it was written against, when it was written against
    /// one. Kept as a hint rather than a requirement: a query outlives the connection
    /// it was first run on, and refusing to open it afterwards would be worse than
    /// opening it beside a different one.</summary>
    public string ConnectionId { get; set; }

    public string ConnectionName { get; set; }
    public string Sql { get; set; }
    public Guid CreatedBy { get; set; }
    public string CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The saved queries one person may see: every shared one, plus their own.</summary>
public sealed class SavedQueryFilter {
    public Guid ViewerId { get; set; }
    public int Limit { get; set; } = 500;
}

/// <summary>Fan-in freshness bookkeeping: when each job was last triggered.</summary>
public sealed class JobTriggerState {
    public string Project { get; set; } = "default";
    public string Environment { get; set; } = "default";
    public string JobName { get; set; }
    public DateTime LastTriggerAt { get; set; }
}

/// <summary>
/// What the monitoring grid may sort on. A whitelist rather than a column name off
/// the wire, because this reaches an ORDER BY.
/// <para>
/// Duration is deliberately absent: it is <c>FinishedAt - StartedAt</c>, null while a
/// run is in flight, and subtracted differently by each provider. Sorting by "took
/// longest" is worth doing when somebody asks, as a computed column, not as a
/// translation gamble.
/// </para>
/// </summary>
public enum RunSort {
    /// <summary>When it started — or, for a run that never did, when it was created.</summary>
    Started,
    Created,
    Project,
    JobName,
    Environment,
    Status,
    Trigger,
}

public sealed class RunQuery {
    /// <summary>
    /// The projects these rows may come from. Required, and there is no "all":
    /// history belongs to its project the same way the project does, and a route
    /// that forgot to say whose history it was asking for is how that leaks. An
    /// empty set matches nothing, which is the correct answer for somebody who can
    /// see no projects.
    /// </summary>
    public required IReadOnlyCollection<string> Projects { get; init; }
    /// <summary>null = all environments.</summary>
    public string Environment { get; set; }
    public string JobName { get; set; }
    /// <summary>The notebook, as stored — the grid's File filter.</summary>
    public string NotebookPath { get; set; }
    public RunStatus? Status { get; set; }
    public RunTrigger? Trigger { get; set; }
    /// <summary>Who pressed run. Only ever matches manual runs.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Inclusive lower bound on the run's start (or creation).</summary>
    public DateTime? Since { get; set; }
    /// <summary>Exclusive upper bound on the run's start (or creation).</summary>
    public DateTime? Until { get; set; }
    public RunSort Sort { get; set; } = RunSort.Started;
    public bool Ascending { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }
}

public sealed class RunStats {
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    /// <summary>
    /// The same counts per project, for the Overview's success rates. A list rather
    /// than a map so the order is the store's and not the serialiser's, and only for
    /// projects that ran something in the window — a row of zeroes for a project
    /// nobody scheduled is noise, not information.
    /// </summary>
    public List<ProjectRunStats> ByProject { get; set; } = new();
}

/// <summary>One project's share of a window.</summary>
public sealed class ProjectRunStats {
    public string Project { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
}
