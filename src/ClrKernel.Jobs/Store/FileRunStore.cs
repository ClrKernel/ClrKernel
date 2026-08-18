using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ClrKernel.Jobs;

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

    /// <summary>A run plus its cells — the shape of one run.json.</summary>
    private sealed class RunRecord {
        public Run Run { get; set; }
        public List<RunCell> Cells { get; set; } = new();
    }

    private string DirectoryFor(Run run) =>
        Path.Combine(_root, run.JobName, run.Id.ToString("N"));

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
        var runs = AllRecords().Select(r => r.Run);
        if (!string.IsNullOrEmpty(query.JobName)) {
            runs = runs.Where(r => string.Equals(r.JobName, query.JobName, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Status is { } status) {
            runs = runs.Where(r => r.Status == status);
        }
        return Task.FromResult<IReadOnlyList<Run>>(
            runs.Skip(query.Offset).Take(query.Limit).ToList());
    }

    public Task<RunStats> GetStatsAsync(TimeSpan window) {
        var since = DateTime.UtcNow - window;
        var runs = AllRecords().Select(r => r.Run).Where(r => r.CreatedAt >= since).ToList();
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

    public Task<Run> GetLastSuccessfulRunAsync(string jobName) =>
        Task.FromResult(AllRecords()
            .Select(r => r.Run)
            .Where(r => string.Equals(r.JobName, jobName, StringComparison.OrdinalIgnoreCase)
                && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefault());

    public Task<bool> HasActiveRunAsync(string jobName) =>
        Task.FromResult(AllRecords().Any(r =>
            string.Equals(r.Run.JobName, jobName, StringComparison.OrdinalIgnoreCase)
            && r.Run.Status is RunStatus.Pending or RunStatus.Running));

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

    public Task<DateTime?> GetLastTriggerAsync(string jobName) =>
        Task.FromResult(ReadTriggers().TryGetValue(jobName, out var at) ? at : (DateTime?)null);

    public async Task SetLastTriggerAsync(string jobName, DateTime triggeredAt) {
        using var _ = await _triggerLock.EnterAsync();
        var triggers = ReadTriggers();
        triggers[jobName] = triggeredAt;
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
