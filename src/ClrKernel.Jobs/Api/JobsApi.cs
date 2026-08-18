using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClrKernel.Jobs;

/// <summary>
/// The HTTP surface: jobs (read and edited as *.jobs.yaml), runs with their live
/// per-cell progress, run artifacts, the notebook tree, and stats. Every
/// client-supplied path goes through <see cref="NotebookTree.SafeResolve"/>.
/// </summary>
public static class JobsApi {
    public static void MapJobsApi(this IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (JobCatalog catalog) => {
            var result = catalog.Load();
            return Results.Ok(new {
                status = result.Errors.Count == 0 ? "ok" : "degraded",
                jobs = result.Jobs.Count,
                notebooksRoot = catalog.NotebooksRoot,
                errors = result.Errors,
                version = typeof(JobsApi).Assembly.GetName().Version?.ToString(),
            });
        });

        // --- notebooks ------------------------------------------------------

        api.MapGet("/notebooks", (JobCatalog catalog) =>
            Results.Ok(NotebookTree.Build(catalog.NotebooksRoot, catalog.Load())));

        api.MapGet("/notebooks/content", (JobCatalog catalog, string path) => {
            var resolved = NotebookTree.SafeResolve(catalog.NotebooksRoot, path);
            if (resolved == null) {
                return Results.BadRequest(new { error = "Path is outside the notebooks root." });
            }
            return File.Exists(resolved)
                ? Results.Text(File.ReadAllText(resolved), "text/plain")
                : Results.NotFound(new { error = $"No such file: {path}" });
        });

        // --- jobs -----------------------------------------------------------

        api.MapGet("/jobs", (JobCatalog catalog) => {
            var result = catalog.Load();
            return Results.Ok(new { jobs = result.Jobs.Select(JobView.From), errors = result.Errors });
        });

        api.MapGet("/jobs/{name}", (JobCatalog catalog, string name) => {
            var job = catalog.Load().Find(name);
            return job == null ? Results.NotFound(new { error = $"No job named '{name}'." })
                : Results.Ok(JobView.From(job));
        });

        api.MapPost("/jobs", (JobCatalog catalog, JobWrite write) => Upsert(catalog, null, write));

        api.MapPut("/jobs/{name}", (JobCatalog catalog, string name, JobWrite write) => Upsert(catalog, name, write));

        api.MapDelete("/jobs/{name}", (JobCatalog catalog, string name) => {
            var job = catalog.Load().Find(name);
            if (job == null) {
                return Results.NotFound(new { error = $"No job named '{name}'." });
            }
            var file = JobsFile.Read(job.SourceFile);
            file.Jobs.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (file.Jobs.Count == 0) {
                // A jobs file with an empty list can't be loaded; remove it instead.
                File.Delete(job.SourceFile);
            } else {
                JobsFile.Write(job.SourceFile, file, catalog.NotebooksRoot);
            }
            return Results.NoContent();
        });

        api.MapPost("/jobs/{name}/run", (JobCatalog catalog, SchedulerService scheduler, string name) => {
            var job = catalog.Load().Find(name);
            if (job == null) {
                return Results.NotFound(new { error = $"No job named '{name}'." });
            }
            var runId = scheduler.TriggerManual(job);
            return runId == null
                ? Results.Conflict(new { error = $"{job.Name} already has a run in flight." })
                : Results.Accepted($"/api/runs/{runId}", new { runId });
        });

        api.MapPost("/jobs/{name}/cancel", (SchedulerService scheduler, string name) =>
            scheduler.TryCancel(name)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { error = $"No in-flight run for '{name}' in this process." }));

        api.MapGet("/jobs/{name}/runs", async (IRunStore store, string name, int? limit, int? offset) =>
            Results.Ok(await store.QueryRunsAsync(new RunQuery {
                JobName = name,
                Limit = Clamp(limit),
                Offset = offset ?? 0,
            })));

        // --- runs -----------------------------------------------------------

        api.MapGet("/runs", async (IRunStore store, string status, int? limit, int? offset) => {
            RunStatus? parsed = null;
            if (!string.IsNullOrEmpty(status)) {
                if (!Enum.TryParse<RunStatus>(status, ignoreCase: true, out var value)) {
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                }
                parsed = value;
            }
            return Results.Ok(await store.QueryRunsAsync(new RunQuery {
                Status = parsed,
                Limit = Clamp(limit),
                Offset = offset ?? 0,
            }));
        });

        api.MapGet("/runs/{id:guid}", async (IRunStore store, Guid id) => {
            var run = await store.GetRunAsync(id);
            return run == null ? Results.NotFound(new { error = $"No run {id}." })
                : Results.Ok(new { run, cells = await store.GetCellsAsync(id) });
        });

        api.MapGet("/runs/{id:guid}/artifact", async (IRunStore store, JobsOptions options, Guid id) =>
            await ServeRunFile(store, options, id, r => r.ArtifactPath, "application/json"));

        api.MapGet("/runs/{id:guid}/log", async (IRunStore store, JobsOptions options, Guid id) =>
            await ServeRunFile(store, options, id, r => r.LogPath, "text/plain"));

        api.MapGet("/stats", async (IRunStore store, int? days) =>
            Results.Ok(await store.GetStatsAsync(TimeSpan.FromDays(Math.Clamp(days ?? 7, 1, 365)))));
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? 50, 1, 500);

    private static async Task<IResult> ServeRunFile(
        IRunStore store, JobsOptions options, Guid id, Func<Run, string> select, string contentType) {
        var run = await store.GetRunAsync(id);
        if (run == null) {
            return Results.NotFound(new { error = $"No run {id}." });
        }
        var relative = select(run);
        // Stored relative to the data dir and written by us, but re-verify: a
        // corrupted row must not turn into an arbitrary file read.
        var path = relative == null ? null : NotebookTree.SafeResolve(options.DataDir, relative);
        return path != null && File.Exists(path)
            ? Results.Text(await File.ReadAllTextAsync(path), contentType)
            : Results.NotFound(new { error = "No such artifact for this run (it may not have been written yet)." });
    }

    private static IResult Upsert(JobCatalog catalog, string existingName, JobWrite write) {
        if (string.IsNullOrWhiteSpace(write?.Name)) {
            return Results.BadRequest(new { error = "A job needs a name." });
        }
        if (string.IsNullOrWhiteSpace(write.Notebook)) {
            return Results.BadRequest(new { error = "A job needs a notebook path." });
        }

        var catalogResult = catalog.Load();
        var existing = existingName != null ? catalogResult.Find(existingName) : null;
        if (existingName != null && existing == null) {
            return Results.NotFound(new { error = $"No job named '{existingName}'." });
        }
        var clash = catalogResult.Find(write.Name);
        if (clash != null && !ReferenceEquals(clash, existing)) {
            return Results.Conflict(new { error = $"A job named '{write.Name}' already exists." });
        }

        var notebook = NotebookTree.SafeResolve(catalog.NotebooksRoot, write.Notebook);
        if (notebook == null) {
            return Results.BadRequest(new { error = "Notebook path is outside the notebooks root." });
        }
        if (!File.Exists(notebook)) {
            return Results.BadRequest(new { error = $"Notebook not found: {write.Notebook}" });
        }

        // New jobs land in <notebook-dir>/<notebook-stem>.jobs.yaml; edits stay put.
        var targetFile = existing?.SourceFile ?? Path.Combine(
            Path.GetDirectoryName(notebook)!,
            Path.GetFileName(notebook).Split('.')[0] + ".jobs.yaml");

        var file = File.Exists(targetFile) ? JobsFile.Read(targetFile) : new JobsFile();
        file.Jobs ??= new List<JobsFileEntry>();
        var entry = existing != null
            ? file.Jobs.FirstOrDefault(e => string.Equals(e.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (entry == null) {
            entry = new JobsFileEntry();
            file.Jobs.Add(entry);
        }

        if (!string.IsNullOrWhiteSpace(write.Cron)) {
            try {
                Cronos.CronExpression.Parse(write.Cron);
            } catch (Cronos.CronFormatException e) {
                return Results.BadRequest(new { error = $"Invalid cron '{write.Cron}': {e.Message}" });
            }
        }

        entry.Name = write.Name;
        entry.Notebook = Path.GetRelativePath(Path.GetDirectoryName(targetFile)!, notebook).Replace('\\', '/');
        entry.Cron = string.IsNullOrWhiteSpace(write.Cron) ? null : write.Cron;
        entry.Enabled = write.Enabled;
        entry.TimeoutSeconds = write.TimeoutSeconds;
        entry.RetryCount = write.RetryCount;
        // JSON values arrive as JsonElement; YAML needs plain CLR scalars or the
        // file ends up holding {valueKind: String} instead of the value.
        entry.Parameters = JsonValues.ToPlain(write.Parameters);
        entry.DependsOn = write.DependsOn;
        entry.Notify = write.Notify;

        try {
            JobsFile.Write(targetFile, file, catalog.NotebooksRoot);
        } catch (Exception e) {
            return Results.BadRequest(new { error = $"Job is not valid: {e.Message}" });
        }

        var saved = catalog.Load().Find(write.Name);
        return existing == null
            ? Results.Created($"/api/jobs/{write.Name}", JobView.From(saved))
            : Results.Ok(JobView.From(saved));
    }
}

/// <summary>A job as the API returns it (absolute paths stay server-side).</summary>
public sealed class JobView {
    public string Name { get; set; }
    public string Notebook { get; set; }
    public string JobsFile { get; set; }
    public string Cron { get; set; }
    public bool Enabled { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int RetryCount { get; set; }
    public IReadOnlyDictionary<string, object> Parameters { get; set; }
    public IReadOnlyList<string> DependsOn { get; set; }
    public NotifyRules Notify { get; set; }

    public static JobView From(JobDefinition job) => job == null ? null : new JobView {
        Name = job.Name,
        Notebook = job.NotebookRelative,
        JobsFile = job.SourceFileRelative,
        Cron = job.Cron,
        Enabled = job.Enabled,
        TimeoutSeconds = job.TimeoutSeconds,
        RetryCount = job.RetryCount,
        Parameters = job.Parameters,
        DependsOn = job.DependsOn,
        Notify = job.Notify,
    };
}

/// <summary>The create/update body for a job.</summary>
public sealed class JobWrite {
    public string Name { get; set; }
    /// <summary>Notebook path relative to the notebooks root.</summary>
    public string Notebook { get; set; }
    public string Cron { get; set; }
    public bool Enabled { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
    public int? RetryCount { get; set; }
    public Dictionary<string, object> Parameters { get; set; }
    public List<string> DependsOn { get; set; }
    public NotifyRules Notify { get; set; }
}
