using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
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
                if (EditableTarget(context, catalog, env, path) is not { } target) {
                    return DevWriteError(context, catalog, env, path);
                }
                if (context.Request.ContentLength is > 2_000_000) {
                    return Results.BadRequest(new { error = "File too large (2 MB limit)." });
                }
                using var reader = new StreamReader(context.Request.Body);
                return SaveToDev(context, target, path, await reader.ReadToEndAsync());
            });

        // The notebook as editable cells, with the languages the kernel can run —
        // the shape the web editor works in. Parsing is NotebookMarkdown's, the same
        // reader/writer `clrkernel run` and the VS Code extension agree with.
        api.MapGet("/envs/{env}/notebooks/cells", async (
            JobCatalog catalog, KernelLanguages kernelLanguages, string env, string path) => {
                if (!catalog.Environments.Contains(env)) {
                    return Results.NotFound(new { error = $"No environment '{env}'." });
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor(env), path);
                if (resolved == null) {
                    return Results.BadRequest(new { error = "Path is outside the notebooks root." });
                }
                if (!resolved.EndsWith(".nb.md", StringComparison.OrdinalIgnoreCase) &&
                    !resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) {
                    return Results.BadRequest(new { error = "Only executable markdown (.nb.md) opens as cells." });
                }
                if (!File.Exists(resolved)) {
                    return Results.NotFound(new { error = $"No such file: {path}" });
                }
                var languages = await kernelLanguages.GetAsync();
                var cells = NotebookMarkdown.Parse(File.ReadAllText(resolved), languages);
                return Results.Ok(new {
                    cells = cells.Select((c, i) => CellView.From(c, i, languages)),
                    languages,
                });
            });

        api.MapPut("/envs/{env}/notebooks/cells", async (
            HttpContext context, JobCatalog catalog, KernelLanguages kernelLanguages, string env, string path) => {
                if (EditableTarget(context, catalog, env, path) is not { } target) {
                    return DevWriteError(context, catalog, env, path);
                }
                if (!target.EndsWith(".nb.md", StringComparison.OrdinalIgnoreCase) &&
                    !target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) {
                    return Results.BadRequest(new { error = "Only executable markdown (.nb.md) saves as cells." });
                }
                CellWrite write;
                try {
                    write = await JsonSerializer.DeserializeAsync<CellWrite>(context.Request.Body, _bodyJson);
                } catch (JsonException e) {
                    return Results.BadRequest(new { error = "Could not read the cells: " + e.Message });
                }
                if (write?.Cells == null) {
                    return Results.BadRequest(new { error = "Body must be { cells: [...] }." });
                }
                if (write.Cells.Count > 1000) {
                    return Results.BadRequest(new { error = "Too many cells (1000 limit)." });
                }
                var languages = await kernelLanguages.GetAsync();
                return SaveToDev(context, target, path, NotebookMarkdown.Serialize(write.Cells.Select(c => c.ToCell(languages))));
            });

        // --- interactive sessions -------------------------------------------
        //
        // Running a cell executes code the request body carried, against a warm
        // kernel that outlives the request. Nothing here writes to the run store:
        // an interactive run leaves no Run rows, so it can never become the green
        // evidence promotion requires.

        api.MapPost("/envs/{env}/notebooks/session", async (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, KernelLanguages kernelLanguages, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                try {
                    // The session seeds kernelLanguages itself, on start and again
                    // whenever #r adds one — one place, so the two cannot drift.
                    var session = await sessions.GetOrStartAsync(resolved, context.RequestAborted);
                    return Results.Ok(SessionView.From(session, false));
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message, kernelLog = sessions.Find(resolved)?.KernelLog() });
                }
            });

        api.MapDelete("/envs/{env}/notebooks/session", (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                return Results.Ok(new { restarted = sessions.Restart(resolved) });
            });

        api.MapPost("/envs/{env}/notebooks/run", async (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                CellWrite request;
                try {
                    request = await JsonSerializer.DeserializeAsync<CellWrite>(context.Request.Body, _bodyJson);
                } catch (JsonException e) {
                    return Results.BadRequest(new { error = "Could not read the cells: " + e.Message });
                }
                if (request?.Cells is not { Count: > 0 }) {
                    return Results.BadRequest(new { error = "Body must be { cells: [...] } with at least one cell." });
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                NotebookSession session;
                try {
                    session = await sessions.GetOrStartAsync(resolved, context.RequestAborted);
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }

                var languages = session.Languages;
                var cells = request.Cells.Select(c => c.ToCell(languages)).ToList();
                var ids = request.Cells.Select((c, i) => c.Id ?? $"run{i}").ToList();
                // The run continues after the response: a long cell must not hold an
                // HTTP request open, and the editor polls status for progress.
                return session.TryStartRun(cells, ids, out _)
                    ? Results.Accepted(value: new { running = ids })
                    : Results.Json(new { error = "This notebook is already running a cell." }, statusCode: 409);
            });

        // What the editor currently has open, so completion and hover have documents
        // to answer about. Called on a debounce while typing, so it must stay cheap
        // and must never start a kernel: a broken configuration would otherwise
        // attempt a spawn every few hundred milliseconds for as long as someone types.
        // The editor starts its session when it opens the notebook.
        api.MapPost("/envs/{env}/notebooks/sync", async (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                SyncWrite request;
                try {
                    request = await JsonSerializer.DeserializeAsync<SyncWrite>(context.Request.Body, _bodyJson);
                } catch (JsonException e) {
                    return Results.BadRequest(new { error = "Could not read the cells: " + e.Message });
                }
                if (request?.Cells == null) {
                    return Results.BadRequest(new { error = "Body must be { cells: [...] }." });
                }
                if (request.Cells.Count > 1000) {
                    return Results.BadRequest(new { error = "Too many cells (1000 limit)." });
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                var session = sessions.Find(resolved);
                if (session == null) {
                    return Results.Ok(new { started = false, sent = 0 });
                }
                try {
                    return Results.Ok(new { started = true, sent = await session.SyncAsync(request.Cells, context.RequestAborted) });
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }
            });

        // One language question about one cell — completion, its lazy documentation,
        // hover, signature help. A whitelisted kind rather than four near-identical
        // routes, and an allowlist rather than a method proxy: completion evaluates
        // against a live REPL, so this is gated exactly like running a cell.
        //
        // The request carries the cell's current text and the session syncs it before
        // asking. The debounced sync is for siblings; a keystroke-triggered completion
        // cannot wait for it, and a position measured against text 300ms behind the
        // cursor answers with the wrong symbol rather than failing.
        api.MapPost("/envs/{env}/notebooks/language", async (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                LanguageRequest request;
                try {
                    request = await JsonSerializer.DeserializeAsync<LanguageRequest>(context.Request.Body, _bodyJson);
                } catch (JsonException e) {
                    return Results.BadRequest(new { error = "Could not read the request: " + e.Message });
                }
                if (!NotebookSession.IsLanguageRequest(request?.Kind)) {
                    return Results.BadRequest(new { error = $"Unknown language request '{request?.Kind}'." });
                }
                if (string.IsNullOrEmpty(request.CellId)) {
                    return Results.BadRequest(new { error = "cellId is required." });
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                var session = sessions.Find(resolved);
                if (session == null) {
                    // Same reasoning as sync: typing must not spawn kernels. The
                    // editor starts the session when it opens the notebook.
                    return Results.Ok(new { started = false, result = (object)null });
                }
                try {
                    var result = await session.LanguageAsync(
                        request.Kind,
                        new NotebookSyncCell {
                            Id = request.CellId,
                            LanguageId = request.LanguageId,
                            Source = request.Source,
                        },
                        request.Line, request.Character, request.Item, context.RequestAborted);
                    return Results.Ok(new { started = true, result });
                } catch (Exception e) {
                    // A language feature is never worth failing the editor over.
                    return Results.Ok(new { started = true, result = (object)null, error = e.Message });
                }
            });

        api.MapGet("/envs/{env}/notebooks/session/status", async (
            HttpContext context, JobCatalog catalog, JobsOptions options, IRunStore store,
            NotebookSessionManager sessions, string env, string path) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                // A scheduled run of this notebook may be in flight in its own kernel;
                // the editor says so rather than leaving the file changing unexplained.
                // Checked before the session lookup, because the warning is most useful
                // when you open the file — which is before any kernel of yours exists.
                // ponytail: Load() re-reads the jobs yaml under the git lock, and the
                // editor polls this ~2.5×/s while a cell runs. Fine for one person's
                // handful of files; cache it per notebook if that ever shows up.
                var scheduled = false;
                foreach (var job in catalog.Load().In(env).Where(j =>
                    string.Equals(j.NotebookPath, resolved, StringComparison.OrdinalIgnoreCase))) {
                    scheduled |= await store.HasActiveRunAsync(job.Environment, job.Name);
                }
                var session = sessions.Find(resolved);
                return session == null
                    ? Results.Ok(new { running = false, started = false, scheduledRunActive = scheduled })
                    : Results.Ok(SessionView.From(session, scheduled));
            });

        // The connection wizard's schema. Answered by the notebook's own kernel so
        // a package `#r`-ed into this session contributes its providers too — the
        // browser never learns what a connection type is, it renders what it is told.
        api.MapGet("/envs/{env}/notebooks/connections", async (
            HttpContext context, JobCatalog catalog, JobsOptions options,
            NotebookSessionManager sessions, string env, string path, string languageId) => {
                if (DenyExecution(context, catalog, options, env, path) is { } denial) {
                    return denial;
                }
                if (string.IsNullOrWhiteSpace(languageId)) {
                    return Results.BadRequest(new { error = "languageId is required." });
                }
                var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
                try {
                    var session = await sessions.GetOrStartAsync(resolved, context.RequestAborted);
                    var reply = await session.DescribeConnectionsAsync(languageId, context.RequestAborted);
                    return Results.Ok(new { providers = reply });
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }
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

    /// <summary>
    /// The absolute path a dev write may target, or null when it may not — the git
    /// workflow is off, the environment is not dev, the path escapes the dev
    /// worktree, or the file is not one we edit. <see cref="DevWriteError"/> says
    /// which, so the two shapes of write (raw text, cells) refuse identically.
    /// </summary>
    private static string EditableTarget(HttpContext context, JobCatalog catalog, string env, string path) {
        if (!catalog.GitLayout || env != "dev" || GitOf(context) == null) {
            return null;
        }
        // Rooted at the dev worktree: the workspace root would resolve prod/… too.
        var resolved = NotebookTree.SafeResolve(catalog.RootFor("dev"), path);
        if (resolved == null) {
            return null;
        }
        return NotebookTree.IsNotebook(resolved) || resolved.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase)
            ? resolved
            : null;
    }

    private static IResult DevWriteError(HttpContext context, JobCatalog catalog, string env, string path) {
        if (!catalog.GitLayout || GitOf(context) == null) {
            return Results.BadRequest(new {
                error = "Editing needs the git workflow — run `clrkernel-jobs git init`.",
            });
        }
        if (env != "dev") {
            return Results.BadRequest(new { error = "prod is read-only — edit in dev and promote." });
        }
        return NotebookTree.SafeResolve(catalog.RootFor("dev"), path) == null
            ? Results.BadRequest(new { error = "Path is outside the dev area." })
            : Results.BadRequest(new { error = "Only notebooks and *.jobs.yaml are editable here." });
    }

    /// <summary>
    /// The single policy check for every endpoint that executes code. Running a
    /// cell is the one place the tool runs code straight from a request body, so
    /// the decision lives in exactly one function.
    /// <para>
    /// The gate: the git workflow must be on, the environment must be dev (prod is
    /// read-only and is not a scratchpad), the path must resolve inside the dev
    /// worktree — and, since an API key is optional, execution is refused when the
    /// server is bound beyond localhost without one. That combination is remote
    /// code execution for anyone who can reach the port.
    /// </para>
    /// </summary>
    private static IResult DenyExecution(
        HttpContext context, JobCatalog catalog, JobsOptions options, string env, string path) {
        if (!catalog.GitLayout) {
            return Results.BadRequest(new {
                error = "Running cells needs the git workflow — run `clrkernel-jobs git init`.",
            });
        }
        if (env != "dev") {
            return Results.BadRequest(new { error = "Cells run in dev only — prod is read-only." });
        }
        if (NotebookTree.SafeResolve(catalog.RootFor("dev"), path) == null) {
            return Results.BadRequest(new { error = "Path is outside the dev area." });
        }
        if (string.IsNullOrEmpty(options.ApiKey) && !IsLocalOnly(options.Urls)) {
            return Results.Json(new {
                error = "Refusing to run cells: the server is listening beyond localhost with no API key. " +
                    "Set --api-key (or CLRKERNEL_JOBS_APIKEY), or bind to localhost.",
            }, statusCode: 403);
        }
        return null;
    }

    /// <summary>True when every configured URL is loopback. Null/empty means the
    /// default bind, which is localhost.</summary>
    internal static bool IsLocalOnly(string urls) {
        if (string.IsNullOrWhiteSpace(urls)) {
            return true;
        }
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) {
                return false; // unparseable: assume the worst
            }
            var host = parsed.Host;
            var loopback = host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
            if (!loopback) {
                return false;
            }
        }
        return true;
    }

    /// <summary>The one committing writer: the file lands and is committed inside a
    /// single git-lock hold, so racing saves cannot commit each other's bytes.</summary>
    private static IResult SaveToDev(HttpContext context, string resolved, string path, string content) {
        var git = GitOf(context);
        git.WithLock(() => {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            File.WriteAllText(resolved, content);
            git.Commit("dev", $"edit {path} via web UI", path);
        });
        return Results.Ok(new { saved = true, commitSha = git.HeadSha("dev") });
    }

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

/// <summary>A notebook cell as the editor sees it: the body as written, the code
/// tag it carried, and the language that tag belongs to (null for C# and prose).</summary>
public sealed class CellView {
    public string Id { get; set; }
    public string Kind { get; set; }
    public string Tag { get; set; }
    public string LanguageId { get; set; }
    public string Source { get; set; }
    public int BlankLinesAfter { get; set; }
    public bool Closed { get; set; }

    public static CellView From(MarkdownCell cell, int index, IReadOnlyList<LanguageDescriptor> languages) => new() {
        Id = "c" + index,
        Kind = cell.Kind == CellKind.Code ? "code" : "markdown",
        Tag = cell.Tag,
        LanguageId = NotebookMarkdown.LanguageForTag(cell.Tag, languages)?.Id,
        Source = cell.Source,
        BlankLinesAfter = cell.BlankLinesAfter,
        Closed = cell.Closed,
    };
}

/// <summary>The editor's open documents: every code cell it is showing.</summary>
public sealed class SyncWrite {
    public List<NotebookSyncCell> Cells { get; set; }
}

/// <summary>One language question about one cell, at one position.</summary>
public sealed class LanguageRequest {
    /// <summary>completion | resolve | hover | signatureHelp.</summary>
    public string Kind { get; set; }
    public string CellId { get; set; }
    public string LanguageId { get; set; }

    /// <summary>The cell as the editor has it right now, so the position means what
    /// the cursor means.</summary>
    public string Source { get; set; }
    public int Line { get; set; }
    public int Character { get; set; }

    /// <summary>For <c>resolve</c> only: the completion item to fill in, round-tripped
    /// from the list that produced it.</summary>
    public JsonElement? Item { get; set; }
}

/// <summary>The editor's save: the whole notebook, as cells.</summary>
public sealed class CellWrite {
    public List<CellEdit> Cells { get; set; }
}

public sealed class CellEdit {
    /// <summary>The editor's cell id, used as the kernel cellId so display
    /// notifications land on the right cell. Ignored on save.</summary>
    public string Id { get; set; }
    public string Kind { get; set; }
    public string Tag { get; set; }
    public string LanguageId { get; set; }
    public string Source { get; set; }
    public int? BlankLinesAfter { get; set; }
    public bool? Closed { get; set; }

    /// <summary>The cell to serialize. A tag the file already carried is kept as
    /// written; only a cell whose language the user just picked (tag absent) gets
    /// one computed, so bash/zsh/sh never collapse into one another.</summary>
    public MarkdownCell ToCell(IReadOnlyList<LanguageDescriptor> languages) {
        var markdown = string.Equals(Kind, "markdown", StringComparison.OrdinalIgnoreCase);
        var tag = Tag;
        if (!markdown && string.IsNullOrEmpty(tag)) {
            var language = languages?.FirstOrDefault(l =>
                string.Equals(l.Id, LanguageId, StringComparison.OrdinalIgnoreCase));
            tag = NotebookMarkdown.TagFor(language);
        }
        return new MarkdownCell {
            Kind = markdown ? CellKind.Markdown : CellKind.Code,
            Tag = markdown ? null : tag,
            Source = Source ?? string.Empty,
            BlankLinesAfter = BlankLinesAfter ?? 1,
            Closed = Closed ?? true,
        };
    }
}

/// <summary>A notebook session as the editor sees it: what the kernel is, what it
/// can run, and what every cell has done so far.</summary>
public sealed class SessionView {
    public string SessionId { get; set; }
    public bool Started { get; set; }
    public bool Running { get; set; }
    public string Kernel { get; set; }
    public string Version { get; set; }
    public bool KernelRestarted { get; set; }
    /// <summary>A scheduled run of this notebook is in flight in its own kernel —
    /// the editor says so rather than letting the file change unexplained.</summary>
    public bool ScheduledRunActive { get; set; }
    public IReadOnlyList<LanguageDescriptor> Languages { get; set; }

    /// <summary>What opens a completion list / signature help, as this kernel declares
    /// it. Passed through rather than restated in the editor: a second copy is one
    /// that goes stale without anyone noticing.</summary>
    public IReadOnlyList<string> CompletionTriggers { get; set; }
    public IReadOnlyList<string> SignatureTriggers { get; set; }

    /// <summary>What the kernel says is wrong in each cell, by cell id. An empty list
    /// is meaningful — it is how a fixed error stops being drawn.</summary>
    public IReadOnlyDictionary<string, JsonElement> Diagnostics { get; set; }

    public Dictionary<string, CellRunView> Cells { get; set; }

    public static SessionView From(NotebookSession session, bool scheduledRunActive) => new() {
        SessionId = session.Id,
        Started = true,
        Running = session.Busy,
        Kernel = session.KernelName,
        Version = session.KernelVersion,
        KernelRestarted = session.KernelRestarted,
        ScheduledRunActive = scheduledRunActive,
        Languages = session.Languages,
        CompletionTriggers = session.CompletionTriggers,
        SignatureTriggers = session.SignatureTriggers,
        Diagnostics = session.Diagnostics(),
        Cells = session.Snapshot().ToDictionary(kv => kv.Key, kv => new CellRunView {
            Status = kv.Value.Status,
            ExecutionCount = kv.Value.ExecutionCount,
            Truncated = kv.Value.Truncated,
            Outputs = kv.Value.Outputs,
        }),
    };
}

public sealed class CellRunView {
    public string Status { get; set; }
    public int? ExecutionCount { get; set; }
    public bool Truncated { get; set; }
    /// <summary>nbformat outputs — the same shapes the run view already renders.</summary>
    public System.Text.Json.Nodes.JsonArray Outputs { get; set; }
}
