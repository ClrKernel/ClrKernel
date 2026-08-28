using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ClrKernel.Studio;

/// <summary>
/// The no-database backend (<c>--store files</c>): each run is a <c>run.json</c>
/// beside its own artifacts, so a run directory is completely self-describing and
/// the history survives being copied or archived wholesale.
/// <para>
/// Queries scan the run directories. ponytail: O(n) over the artifacts tree, which
/// is fine at human scale — if a history ever grows past a few thousand runs, add a
/// per-job index file rather than reaching for a database.
/// </para>
/// </summary>
public sealed class FileRunStore : IRunStore {
    private readonly string _root;
    private readonly string _triggersPath;
    // Serialises the read-modify-write of the shared trigger file.
    private readonly SemaphoreSlimScope _triggerLock = new();
    private readonly ConcurrentDictionary<Guid, string> _runPaths = new();

    private static readonly JsonSerializerOptions _json = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public FileRunStore(JobsOptions options) {
        _root = options.ArtifactsDir;
        _triggersPath = Path.Combine(options.DataDir, "triggers.json");
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// The file-store half of the 0.10 <c>dev</c> → <c>test</c> rename — the same
    /// migration the relational stores get as SQL. Runs live under
    /// <c>artifacts/&lt;environment&gt;/…</c> *and* name their environment inside
    /// run.json, so both have to move or a promotion gate stops finding its evidence.
    /// </summary>
    internal void MigrateLegacyEnvironment() {
        var legacy = Path.Combine(_root, GitService.LegacyTestBranch);
        var target = Path.Combine(_root, GitService.TestBranch);
        if (!Directory.Exists(legacy) || Directory.Exists(target)) {
            return;
        }
        Directory.Move(legacy, target);

        // The fan-in clock is keyed "<environment>/<job>" in one shared file.
        var triggers = ReadTriggers();
        var prefix = GitService.LegacyTestBranch + "/";
        var moved = triggers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in moved) {
            triggers[GitService.TestBranch + "/" + key[prefix.Length..]] = triggers[key];
            triggers.Remove(key);
        }
        if (moved.Count > 0) {
            File.WriteAllText(_triggersPath, JsonSerializer.Serialize(triggers, _json));
        }

        foreach (var path in Directory.EnumerateFiles(target, "run.json", SearchOption.AllDirectories)) {
            var record = Read(path);
            if (record?.Run == null
                || !string.Equals(record.Run.Environment, GitService.LegacyTestBranch, StringComparison.Ordinal)) {
                continue;
            }
            record.Run.Environment = GitService.TestBranch;
            File.WriteAllText(path, JsonSerializer.Serialize(record, _json));
        }
    }

    /// <summary>A run plus its cells — the shape of one run.json.</summary>
    private sealed class RunRecord {
        public Run Run { get; set; }
        public List<RunCell> Cells { get; set; } = new();
    }

    // ponytail: the directory is <environment>/<job>/<id>, with no project segment —
    // the project is inside run.json and every query filters on it. Adding one would
    // split the tree between old and new runs for no gain; `serve` needs a database
    // anyway, so a multi-project file store is a command-line-only arrangement.
    private string DirectoryFor(Run run) =>
        Path.Combine(_root, run.Environment ?? "default", run.JobName, run.Id.ToString("N"));

    private string PathFor(Run run) => Path.Combine(DirectoryFor(run), "run.json");

    private async Task WriteAsync(RunRecord record) {
        var path = PathFor(record.Run);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write-then-move so a crash mid-write can't leave a half-parsed record.
        var staging = path + ".tmp";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(record, _json));
        File.Move(staging, path, overwrite: true);
        _runPaths[record.Run.Id] = path;
    }

    private static RunRecord Read(string path) {
        try {
            return JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), _json);
        } catch (Exception) {
            // A truncated or hand-edited record must not break the whole listing.
            return null;
        }
    }

    private IEnumerable<string> AllRunFiles() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "run.json", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    private RunRecord Find(Guid id) {
        if (_runPaths.TryGetValue(id, out var known) && File.Exists(known)) {
            return Read(known);
        }
        foreach (var path in AllRunFiles()) {
            var record = Read(path);
            if (record?.Run?.Id == id) {
                _runPaths[id] = path;
                return record;
            }
        }
        return null;
    }

    private IEnumerable<RunRecord> AllRecords() =>
        AllRunFiles()
            .Select(Read)
            .Where(r => r?.Run != null)
            .OrderByDescending(r => r.Run.CreatedAt);

    public async Task<Run> CreateRunAsync(Run run) {
        await WriteAsync(new RunRecord { Run = run });
        return run;
    }

    public async Task UpdateRunAsync(Run run) {
        var record = Find(run.Id) ?? new RunRecord();
        record.Run = run;
        await WriteAsync(record);
    }

    public Task<Run> GetRunAsync(Guid id) => Task.FromResult(Find(id)?.Run);

    public Task<IReadOnlyList<Run>> QueryRunsAsync(RunQuery query) {
        var runs = AllRecords().Select(r => r.Run)
            .Where(r => query.Projects.Contains(r.Project ?? "default", StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(query.Environment)) {
            runs = runs.Where(r => string.Equals(r.Environment, query.Environment, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.JobName)) {
            runs = runs.Where(r => string.Equals(r.JobName, query.JobName, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.NotebookPath)) {
            runs = runs.Where(r => string.Equals(r.NotebookPath, query.NotebookPath, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Status is { } status) {
            runs = runs.Where(r => r.Status == status);
        }
        if (query.Trigger is { } trigger) {
            runs = runs.Where(r => r.Trigger == trigger);
        }
        if (query.ActorId is { } actor) {
            runs = runs.Where(r => r.ActorId == actor);
        }
        if (query.Since is { } since) {
            runs = runs.Where(r => (r.StartedAt ?? r.CreatedAt) >= since);
        }
        if (query.Until is { } until) {
            runs = runs.Where(r => (r.StartedAt ?? r.CreatedAt) < until);
        }
        return Task.FromResult<IReadOnlyList<Run>>(
            Ordered(runs, query).Skip(query.Offset).Take(query.Limit).ToList());
    }

    /// <summary>The same order <see cref="EfRunStore"/> produces — see the note there.</summary>
    private static IEnumerable<Run> Ordered(IEnumerable<Run> runs, RunQuery query) {
        var ascending = query.Ascending;
        IOrderedEnumerable<Run> sorted = query.Sort switch {
            RunSort.Created => By(runs, r => r.CreatedAt, ascending),
            RunSort.Project => By(runs, r => r.Project ?? "default", ascending),
            RunSort.JobName => By(runs, r => r.JobName ?? "", ascending),
            RunSort.Environment => By(runs, r => r.Environment ?? "", ascending),
            // ToString, not the enum's number: the relational stores keep these as
            // text and ORDER BY sorts them alphabetically, and the two backends
            // returning different orders for the same query is the bug this file
            // exists to not have.
            RunSort.Status => By(runs, r => r.Status.ToString(), ascending),
            RunSort.Trigger => By(runs, r => r.Trigger.ToString(), ascending),
            _ => By(runs, r => r.StartedAt ?? r.CreatedAt, ascending),
        };
        return query.Sort == RunSort.Created
            ? sorted.ThenByDescending(r => r.Id)
            : sorted.ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
    }

    private static IOrderedEnumerable<Run> By<TKey>(
        IEnumerable<Run> runs, Func<Run, TKey> key, bool ascending) =>
        ascending ? runs.OrderBy(key) : runs.OrderByDescending(key);

    public Task<RunStats> GetStatsAsync(
        TimeSpan window, IReadOnlyCollection<string> projects = null) {
        var since = DateTime.UtcNow - window;
        var runs = AllRecords().Select(r => r.Run)
            .Where(r => r.CreatedAt >= since
                && (projects == null || projects.Contains(r.Project ?? "default")))
            .ToList();
        return Task.FromResult(new RunStats {
            Total = runs.Count,
            Succeeded = runs.Count(r => r.Status == RunStatus.Succeeded),
            Failed = runs.Count(r => r.Status is RunStatus.Failed or RunStatus.TimedOut),
            ByStatus = runs.GroupBy(r => r.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()),
        });
    }

    public async Task SaveCellsAsync(Guid runId, IReadOnlyList<RunCell> cells) {
        var record = Find(runId);
        if (record == null) {
            return;
        }
        record.Cells = cells.ToList();
        await WriteAsync(record);
    }

    public async Task UpdateCellAsync(RunCell cell) {
        var record = Find(cell.RunId);
        if (record == null) {
            return;
        }
        var index = record.Cells.FindIndex(c => c.CellIndex == cell.CellIndex);
        if (index >= 0) {
            record.Cells[index] = cell;
        } else {
            record.Cells.Add(cell);
        }
        await WriteAsync(record);
    }

    public Task<IReadOnlyList<RunCell>> GetCellsAsync(Guid runId) =>
        Task.FromResult<IReadOnlyList<RunCell>>(
            (Find(runId)?.Cells ?? new List<RunCell>()).OrderBy(c => c.CellIndex).ToList());

    private static bool Matches(Run run, string project, string environment, string jobName) =>
        string.Equals(run.Project ?? "default", project, StringComparison.OrdinalIgnoreCase)
        && string.Equals(run.Environment ?? "default", environment, StringComparison.OrdinalIgnoreCase)
        && string.Equals(run.JobName, jobName, StringComparison.OrdinalIgnoreCase);

    public Task<Run> GetLastSuccessfulRunAsync(string project, string environment, string jobName) =>
        Task.FromResult(AllRecords()
            .Select(r => r.Run)
            .Where(r => Matches(r, project, environment, jobName) && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefault());

    public Task<bool> HasActiveRunAsync(string project, string environment, string jobName) =>
        Task.FromResult(AllRecords().Any(r =>
            Matches(r.Run, project, environment, jobName)
            && r.Run.Status is RunStatus.Pending or RunStatus.Running));

    // --- the audit of hand-driven runs --------------------------------------
    //
    // One line of JSON each, appended. `serve` needs a database, so nothing here
    // can actually reach these — but an audit with a silent hole in it is worse
    // than no audit, so the file store keeps one rather than pretending.

    private string ManualRunsPath => Path.Combine(_root, "..", "manual-runs.jsonl");

    public async Task StartManualRunAsync(ManualRun run) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ManualRunsPath))!);
        await File.AppendAllTextAsync(
            ManualRunsPath, JsonSerializer.Serialize(run, _compact) + "\n");
    }

    public async Task FinishManualRunAsync(
        Guid id, string outcome, string errorSummary, DateTime finishedAt) {
        var all = ReadManualRuns();
        var run = all.FirstOrDefault(r => r.Id == id);
        if (run == null) {
            return;
        }
        run.Outcome = outcome;
        run.ErrorSummary = errorSummary;
        run.FinishedAt = finishedAt;
        await File.WriteAllTextAsync(ManualRunsPath,
            string.Concat(all.Select(r => JsonSerializer.Serialize(r, _compact) + "\n")));
    }

    public Task<IReadOnlyList<ManualRun>> QueryManualRunsAsync(ManualRunQuery query) {
        var runs = ReadManualRuns().AsEnumerable();
        if (!string.IsNullOrEmpty(query.Project)) {
            runs = runs.Where(r => string.Equals(r.Project, query.Project, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.Environment)) {
            runs = runs.Where(r => string.Equals(r.Environment, query.Environment, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(query.NotebookPath)) {
            runs = runs.Where(r => string.Equals(r.NotebookPath, query.NotebookPath, StringComparison.OrdinalIgnoreCase));
        }
        return Task.FromResult<IReadOnlyList<ManualRun>>(
            runs.OrderByDescending(r => r.StartedAt).Take(query.Limit).ToList());
    }

    private string QueryAuditPath => Path.Combine(_root, "..", "connection-queries.jsonl");

    private string PromotionAuditPath => Path.Combine(_root, "promotions.jsonl");

    public async Task RecordPromotionAsync(PromotionAudit audit) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PromotionAuditPath))!);
        await File.AppendAllTextAsync(
            PromotionAuditPath, JsonSerializer.Serialize(audit, _compact) + "\n");
    }

    public Task<IReadOnlyList<PromotionAudit>> PromotionAuditAsync(PromotionAuditQuery query) {
        var audits = ReadLines<PromotionAudit>(PromotionAuditPath).AsEnumerable();
        if (!string.IsNullOrEmpty(query.Project)) {
            audits = audits.Where(a =>
                string.Equals(a.Project, query.Project, StringComparison.OrdinalIgnoreCase));
        }
        if (query.UnschedulesOnly) {
            audits = audits.Where(a => !string.IsNullOrEmpty(a.Unscheduled));
        }
        return Task.FromResult<IReadOnlyList<PromotionAudit>>(audits
            .OrderByDescending(a => a.PromotedAt)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToList());
    }

    public async Task RecordQueryAsync(QueryAudit audit) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(QueryAuditPath))!);
        await File.AppendAllTextAsync(
            QueryAuditPath, JsonSerializer.Serialize(audit, _compact) + "\n");
    }

    public Task<IReadOnlyList<QueryAudit>> QueryAuditAsync(QueryAuditQuery query) {
        var audits = ReadQueryAudits().AsEnumerable();
        if (!string.IsNullOrEmpty(query.ConnectionId)) {
            audits = audits.Where(a =>
                string.Equals(a.ConnectionId, query.ConnectionId, StringComparison.OrdinalIgnoreCase));
        }
        // The same rule the EF store applies, and it has to be the same: a private
        // connection's rows are its actor's alone.
        audits = audits.Where(a =>
            a.ActorId == query.ViewerId
            || (!string.Equals(a.Scope, "private", StringComparison.OrdinalIgnoreCase)
                && query.ViewerIsAdmin));
        return Task.FromResult<IReadOnlyList<QueryAudit>>(
            audits.OrderByDescending(a => a.StartedAt).Take(query.Limit).ToList());
    }

    private string SavedQueriesPath => Path.Combine(_root, "..", "saved-queries.json");

    public async Task SaveQueryAsync(SavedQuery query) {
        var all = ReadSavedQueries().Where(q => q.Id != query.Id).ToList();
        all.Add(query);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SavedQueriesPath))!);
        await File.WriteAllTextAsync(SavedQueriesPath, JsonSerializer.Serialize(all, _compact));
    }

    public Task<IReadOnlyList<SavedQuery>> SavedQueriesAsync(SavedQueryFilter filter) =>
        Task.FromResult<IReadOnlyList<SavedQuery>>(
            ReadSavedQueries()
                .Where(q => !IsPrivate(q) || q.OwnerId == filter.ViewerId)
                .OrderBy(q => q.Scope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                .Take(filter.Limit)
                .ToList());

    public Task<SavedQuery> SavedQueryAsync(Guid id, Guid viewerId) =>
        Task.FromResult(ReadSavedQueries().FirstOrDefault(q =>
            q.Id == id && (!IsPrivate(q) || q.OwnerId == viewerId)));

    public async Task<bool> DeleteSavedQueryAsync(Guid id) {
        var all = ReadSavedQueries();
        var kept = all.Where(q => q.Id != id).ToList();
        if (kept.Count == all.Count) {
            return false;
        }
        await File.WriteAllTextAsync(SavedQueriesPath, JsonSerializer.Serialize(kept, _compact));
        return true;
    }

    private static bool IsPrivate(SavedQuery query) =>
        string.Equals(query.Scope, "private", StringComparison.OrdinalIgnoreCase);

    private List<SavedQuery> ReadSavedQueries() {
        if (!File.Exists(SavedQueriesPath)) {
            return new List<SavedQuery>();
        }
        try {
            return JsonSerializer.Deserialize<List<SavedQuery>>(
                File.ReadAllText(SavedQueriesPath), _compact) ?? new List<SavedQuery>();
        } catch (JsonException) {
            return new List<SavedQuery>();
        }
    }

    private List<QueryAudit> ReadQueryAudits() => ReadLines<QueryAudit>(QueryAuditPath);

    /// <summary>
    /// One JSON object per line, skipping what will not parse — a truncated final
    /// line from a crash must not lose the rest of the log.
    /// </summary>
    private List<T> ReadLines<T>(string path) {
        var items = new List<T>();
        if (!File.Exists(path)) {
            return items;
        }
        foreach (var line in File.ReadAllLines(path)) {
            if (line.Trim().Length == 0) {
                continue;
            }
            try {
                if (JsonSerializer.Deserialize<T>(line, _compact) is { } item) {
                    items.Add(item);
                }
            } catch (JsonException) {
                // Deliberately swallowed; see above.
            }
        }
        return items;
    }

    private static readonly JsonSerializerOptions _compact = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private List<ManualRun> ReadManualRuns() {
        if (!File.Exists(ManualRunsPath)) {
            return new List<ManualRun>();
        }
        var runs = new List<ManualRun>();
        foreach (var line in File.ReadAllLines(ManualRunsPath)) {
            if (line.Trim().Length == 0) {
                continue;
            }
            try {
                if (JsonSerializer.Deserialize<ManualRun>(line, _compact) is { } run) {
                    runs.Add(run);
                }
            } catch (JsonException) {
                // A truncated final line from a crash must not lose the rest.
            }
        }
        return runs;
    }

    // --- trigger state (one small shared file) ------------------------------

    private Dictionary<string, DateTime> ReadTriggers() {
        if (!File.Exists(_triggersPath)) {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
        try {
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_triggersPath), _json)
                ?? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        } catch (Exception) {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The default project keeps the two-part key it has always had, so an existing
    /// triggers.json still answers after the upgrade; anything registered later is
    /// namespaced by its slug.
    /// </summary>
    private static string TriggerKey(string project, string environment, string jobName) =>
        string.Equals(project ?? "default", "default", StringComparison.OrdinalIgnoreCase)
            ? $"{environment}/{jobName}"
            : $"{project}/{environment}/{jobName}";

    public Task<DateTime?> GetLastTriggerAsync(string project, string environment, string jobName) =>
        Task.FromResult(ReadTriggers().TryGetValue(TriggerKey(project, environment, jobName), out var at)
            ? at : (DateTime?)null);

    public async Task SetLastTriggerAsync(
        string project, string environment, string jobName, DateTime triggeredAt) {
        using var _ = await _triggerLock.EnterAsync();
        var triggers = ReadTriggers();
        triggers[TriggerKey(project, environment, jobName)] = triggeredAt;
        Directory.CreateDirectory(Path.GetDirectoryName(_triggersPath)!);
        var staging = _triggersPath + ".tmp";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(triggers, _json));
        File.Move(staging, _triggersPath, overwrite: true);
    }

    public async Task<int> MarkOrphansFailedAsync() {
        var orphans = AllRecords()
            .Where(r => r.Run.Status is RunStatus.Pending or RunStatus.Running)
            .ToList();
        foreach (var record in orphans) {
            record.Run.Status = RunStatus.Failed;
            record.Run.ErrorSummary = "Orphaned by shutdown.";
            record.Run.FinishedAt ??= DateTime.UtcNow;
            await WriteAsync(record);
        }
        return orphans.Count;
    }

    /// <summary>A tiny async lock; SemaphoreSlim with a using-scope release.</summary>
    private sealed class SemaphoreSlimScope {
        private readonly System.Threading.SemaphoreSlim _semaphore = new(1, 1);

        public async Task<IDisposable> EnterAsync() {
            await _semaphore.WaitAsync();
            return new Releaser(_semaphore);
        }

        private sealed class Releaser : IDisposable {
            private readonly System.Threading.SemaphoreSlim _semaphore;
            public Releaser(System.Threading.SemaphoreSlim semaphore) => _semaphore = semaphore;
            public void Dispose() => _semaphore.Release();
        }
    }
}
