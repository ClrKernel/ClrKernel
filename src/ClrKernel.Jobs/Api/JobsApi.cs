using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions _bodyJson = new(JsonSerializerDefaults.Web);

    public static void MapJobsApi(this IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (HttpContext context, JobCatalog catalog) => {
            var result = catalog.Load();
            var git = context.RequestServices.GetService(typeof(GitService)) as GitService;
            return Results.Ok(new {
                status = result.Errors.Count == 0 ? "ok" : "degraded",
                jobs = result.Jobs.Count,
                notebooksRoot = catalog.NotebooksRoot,
                environments = catalog.Environments,
                gitEnabled = git != null,
                // A push that can never succeed must not be silent divergence.
                lastPush = git?.LastPush.At == null ? null : new {
                    at = git.LastPush.At,
                    ok = git.LastPush.Ok,
                    error = git.LastPush.Error,
                },
                errors = result.Errors,
                version = typeof(JobsApi).Assembly.GetName().Version?.ToString(),
            });
        });

        // --- notebooks ------------------------------------------------------

        api.MapGet("/notebooks", (JobCatalog catalog) => {
            var result = catalog.Load();
            return Results.Ok(new {
                environments = catalog.Environments.Select(env => new {
                    name = env,
                    tree = Directory.Exists(catalog.RootFor(env))
                        ? NotebookTree.Build(catalog.RootFor(env), result, env)
                        : null,
                }),
            });
        });

        api.MapGet("/envs/{env}/notebooks/content", (JobCatalog catalog, string env, string path) => {
            if (!catalog.Environments.Contains(env)) {
                return Results.NotFound(new { error = $"No environment '{env}'." });
            }
            // Rooted at the environment's own tree — resolving against the workspace
            // would happily reach across into the other worktree.
            var resolved = NotebookTree.SafeResolve(catalog.RootFor(env), path);
            if (resolved == null) {
                return Results.BadRequest(new { error = "Path is outside the notebooks root." });
            }
            return File.Exists(resolved)
                ? Results.Text(File.ReadAllText(resolved), "text/plain")
                : Results.NotFound(new { error = $"No such file: {path}" });
        });

        api.MapPut("/envs/{env}/notebooks/content", async (
            HttpContext context, JobCatalog catalog, string env, string path) => {
                if (!catalog.GitLayout) {
                    return Results.BadRequest(new {
                        error = "Editing needs the git workflow — run `clrkernel-jobs git init`.",
                    });
                }
                if (env != "dev") {
                    return Results.BadRequest(new { error = "prod is read-only — edit in dev and promote." });
                }
                // Rooted at the dev worktree: the workspace root would resolve prod/… too.
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                if (resolved == null) {
                    return Results.BadRequest(new { error = "Path is outside the dev area." });
                }
                if (!NotebookTree.IsNotebook(resolved) && !resolved.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase)) {
                    return Results.BadRequest(new { error = "Only notebooks and *.jobs.yaml are editable here." });
                }
                if (context.Request.ContentLength is > 2_000_000) {
                    return Results.BadRequest(new { error = "File too large (2 MB limit)." });
                }
                using var reader = new StreamReader(context.Request.Body);
                var content = await reader.ReadToEndAsync();

                var git = context.RequestServices.GetService(typeof(GitService)) as GitService;
                git!.WithLock(() => {
                    Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                    File.WriteAllText(resolved, content);
                    git.Commit("dev", $"edit {path} via web UI", path);
                });
                return Results.Ok(new { saved = true, commitSha = git.HeadSha("dev") });
            });

        api.MapGet("/envs/dev/notebooks/promotion", async (
            HttpContext context, JobCatalog catalog, IRunStore store, string path) => {
                var git = GitOf(context);
                if (git == null || !catalog.GitLayout) {
                    return Results.BadRequest(new { error = "The git workflow is not enabled." });
                }
                if (NotebookTree.SafeResolve(catalog.RootFor("dev"), path) == null) {
                    return Results.BadRequest(new { error = "Path is outside the dev area." });
                }
                return Results.Ok(await Promotion.CheckAsync(catalog, git, store, path));
            });

        api.MapPost("/envs/dev/notebooks/promote", async (
            HttpContext context, JobCatalog catalog, IRunStore store, JobsOptions options, string path) => {
                var git = GitOf(context);
                if (git == null || !catalog.GitLayout) {
                    return Results.BadRequest(new { error = "The git workflow is not enabled." });
                }
                if (NotebookTree.SafeResolve(catalog.RootFor("dev"), path) == null) {
                    return Results.BadRequest(new { error = "Path is outside the dev area." });
                }
                // Re-check inside the request: the button may be stale.
                var eligibility = await Promotion.CheckAsync(catalog, git, store, path);
                if (!eligibility.Eligible) {
                    return Results.Conflict(new { error = "Not eligible.", reasons = eligibility.Reasons });
                }
                var sha = Promotion.Apply(git, eligibility, path);
                git.TryPush(options.GitPushRemote);
                return Results.Ok(new { promoted = true, commitSha = sha, paths = eligibility.Paths });
            });

        api.MapGet("/git/diff", (HttpContext context, JobCatalog catalog, string path) => {
            var git = context.RequestServices.GetService(typeof(GitService)) as GitService;
            if (git == null || !catalog.GitLayout) {
                return Results.BadRequest(new { error = "The git workflow is not enabled." });
            }
            if (NotebookTree.SafeResolve(catalog.RootFor("dev"), path) == null) {
                return Results.BadRequest(new { error = "Path is outside the dev area." });
            }
            return Results.Text(git.UnifiedDiff(path), "text/plain");
        });

        // --- jobs -----------------------------------------------------------

        api.MapGet("/jobs", (JobCatalog catalog) => {
            var result = catalog.Load();
            return Results.Ok(new { jobs = result.Jobs.Select(JobView.From), errors = result.Errors });
        });

        api.MapGet("/envs/{env}/jobs/{name}", (JobCatalog catalog, string env, string name) => {
            var job = catalog.Load().Find(env, name);
            return job == null ? Results.NotFound(new { error = $"No job named '{name}' in {env}." })
                : Results.Ok(JobView.From(job));
        });

        api.MapPost("/envs/{env}/jobs", (HttpContext context, JobCatalog catalog, string env, JobWrite write) =>
            Upsert(catalog, GitOf(context), env, null, write));

        api.MapPut("/envs/{env}/jobs/{name}", (
            HttpContext context, JobCatalog catalog, string env, string name, JobWrite write) =>
            Upsert(catalog, GitOf(context), env, name, write));

        api.MapDelete("/envs/{env}/jobs/{name}", (HttpContext context, JobCatalog catalog, string env, string name) => {
            var job = catalog.Load().Find(env, name);
            if (job == null) {
                return Results.NotFound(new { error = $"No job named '{name}'." });
            }
            if (catalog.GitLayout && env != "dev") {
                return Results.BadRequest(new { error = "prod is read-only — delete in dev and promote." });
            }
            var git = GitOf(context);
            void Mutate() {
                var file = JobsFile.Read(job.SourceFile);
                file.Jobs.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (file.Jobs.Count == 0) {
                    // A jobs file with an empty list can't be loaded; remove it instead.
                    File.Delete(job.SourceFile);
                } else {
                    JobsFile.Write(job.SourceFile, file, catalog.RootFor(env));
                }
                git?.Commit("dev", $"delete job {name} via web UI", job.SourceFileRelative);
            }
            if (git != null && catalog.GitLayout) {
                git.WithLock(Mutate);
            } else {
                Mutate();
            }
            return Results.NoContent();
        });

        // The body is read by hand rather than bound: a [FromBody] parameter adds a
        // content-type constraint to route matching, which makes a plain
        // `curl -X POST …/run` (no body, no headers) miss the route entirely.
        api.MapPost("/envs/{env}/jobs/{name}/run", async (
            HttpContext context, JobCatalog catalog, SchedulerService scheduler, string env, string name) => {
                var job = catalog.Load().Find(env, name);
                if (job == null) {
                    return Results.NotFound(new { error = $"No job named '{name}' in {env}." });
                }

                RunOverrides overrides = null;
                if (context.Request.ContentLength is > 0) {
                    try {
                        overrides = await JsonSerializer.DeserializeAsync<RunOverrides>(
                            context.Request.Body, _bodyJson);
                    } catch (JsonException e) {
                        return Results.BadRequest(new { error = $"Body is not valid JSON: {e.Message}" });
                    }
                }

                // Ad-hoc parameters merge over the job's own for this run only; the
                // *.jobs.yaml is untouched.
                if (overrides?.Parameters is { Count: > 0 } extra) {
                    var merged = new Dictionary<string, object>(job.Parameters, StringComparer.Ordinal);
                    foreach (var kv in JsonValues.ToPlain(extra)) {
                        merged[kv.Key] = kv.Value;
                    }
                    job = job.With(merged);
                }
                var runId = scheduler.TriggerManual(job, overrides?.Parameters is { Count: > 0 });
                return runId == null
                    ? Results.Conflict(new { error = $"{job.Name} already has a run in flight." })
                    : Results.Accepted($"/api/runs/{runId}", new { runId });
            });

        api.MapPost("/envs/{env}/jobs/{name}/cancel", (SchedulerService scheduler, string env, string name) =>
            scheduler.TryCancel(env, name)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { error = $"No in-flight run for '{name}' in {env}." }));

        api.MapGet("/envs/{env}/jobs/{name}/runs", async (
            IRunStore store, string env, string name, int? limit, int? offset) =>
            Results.Ok(await store.QueryRunsAsync(new RunQuery {
                Environment = env,
                JobName = name,
                Limit = Clamp(limit),
                Offset = offset ?? 0,
            })));

        // --- runs -----------------------------------------------------------

        api.MapGet("/runs", async (IRunStore store, string status, string env, int? limit, int? offset) => {
            RunStatus? parsed = null;
            if (!string.IsNullOrEmpty(status)) {
                if (!Enum.TryParse<RunStatus>(status, ignoreCase: true, out var value)) {
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                }
                parsed = value;
            }
            return Results.Ok(await store.QueryRunsAsync(new RunQuery {
                Environment = env,
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

        // --- notification channels -------------------------------------------

        api.MapGet("/channels", (JobCatalog catalog) => {
            var channels = NotificationChannels.Load(catalog.NotebooksRoot);
            return Results.Ok(new {
                // Secret *references* are safe to show; the secrets never leave the host.
                channels = channels.Channels.Select(c => new {
                    c.Name,
                    c.Type,
                    c.Url,
                    c.Host,
                    c.Port,
                    c.From,
                    c.To,
                    c.User,
                    c.BearerSecretRef,
                    c.PasswordSecretRef,
                }),
                errors = channels.Validate(),
            });
        });

        api.MapPut("/channels", (JobCatalog catalog, NotificationChannels channels) => {
            if (channels?.Channels == null) {
                return Results.BadRequest(new { error = "Expected a channels list." });
            }
            try {
                NotificationChannels.Save(catalog.NotebooksRoot, channels);
            } catch (InvalidDataException e) {
                return Results.BadRequest(new { error = e.Message });
            }
            return Results.Ok(new { channels = channels.Channels.Count });
        });

        api.MapPost("/channels/{name}/test", async (JobCatalog catalog, Notifier notifier, string name) => {
            var channel = NotificationChannels.Load(catalog.NotebooksRoot).Find(name);
            if (channel == null) {
                return Results.NotFound(new {
                    error = $"No channel named '{name}' in {NotificationChannels.FileName}.",
                });
            }
            try {
                await notifier.SendAsync(channel, Notifier.Message.Test(channel.Name));
                return Results.Ok(new { sent = true });
            } catch (Exception e) {
                // The point of a test button is seeing why it failed.
                return Results.BadRequest(new { error = e.Message });
            }
        });

        // --- settings ---------------------------------------------------------

        api.MapGet("/settings", (SettingsRegistry registry) =>
            Results.Ok(new { sections = registry.Sections }));

        api.MapPut("/settings/{section}", (SettingsRegistry registry, string section,
            Dictionary<string, JsonElement> values) => {
                var error = registry.Write(section, values);
                return error == null
                    ? Results.Ok(new { saved = true, restartRequired = true })
                    : Results.BadRequest(new { error });
            });

        // A mistyped API route must answer 404 JSON, not fall through to the SPA's
        // index.html fallback (which would hand a client 200 text/html).
        api.MapFallback(() => Results.NotFound(new { error = "No such API endpoint." }));
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? 50, 1, 500);

    private static GitService GitOf(HttpContext context) =>
        context.RequestServices.GetService(typeof(GitService)) as GitService;

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

    private static IResult Upsert(
        JobCatalog catalog, GitService git, string env, string existingName, JobWrite write) {
        if (!catalog.Environments.Contains(env)) {
            return Results.NotFound(new { error = $"No environment '{env}'." });
        }
        if (catalog.GitLayout && env != "dev") {
            return Results.BadRequest(new {
                error = "prod is read-only — edit in dev and promote.",
            });
        }
        if (string.IsNullOrWhiteSpace(write?.Name)) {
            return Results.BadRequest(new { error = "A job needs a name." });
        }
        if (string.IsNullOrWhiteSpace(write.Notebook)) {
            return Results.BadRequest(new { error = "A job needs a notebook path." });
        }

        var catalogResult = catalog.Load();
        var existing = existingName != null ? catalogResult.Find(env, existingName) : null;
        if (existingName != null && existing == null) {
            return Results.NotFound(new { error = $"No job named '{existingName}' in {env}." });
        }
        var clash = catalogResult.Find(env, write.Name);
        if (clash != null && !ReferenceEquals(clash, existing)) {
            return Results.Conflict(new { error = $"A job named '{write.Name}' already exists." });
        }

        var notebook = NotebookTree.SafeResolve(catalog.RootFor(env), write.Notebook);
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
            if (git != null && catalog.GitLayout) {
                var relative = Path.GetRelativePath(catalog.RootFor(env), targetFile).Replace('\\', '/');
                git.WithLock(() => {
                    JobsFile.Write(targetFile, file, catalog.RootFor(env));
                    git.Commit("dev", $"edit job {write.Name} via web UI", relative);
                });
            } else {
                JobsFile.Write(targetFile, file, catalog.RootFor(env));
            }
        } catch (Exception e) {
            return Results.BadRequest(new { error = $"Job is not valid: {e.Message}" });
        }

        var saved = catalog.Load().Find(env, write.Name);
        return existing == null
            ? Results.Created($"/api/envs/{env}/jobs/{write.Name}", JobView.From(saved))
            : Results.Ok(JobView.From(saved));
    }
}

/// <summary>A job as the API returns it (absolute paths stay server-side).</summary>
public sealed class JobView {
    public string Environment { get; set; }
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
        Environment = job.Environment,
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

/// <summary>Optional body for an ad-hoc run: parameters for this run only.</summary>
public sealed class RunOverrides {
    public Dictionary<string, object> Parameters { get; set; }
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
