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

        // Everything that acts on notebooks or jobs is scoped to one project and one
        // of its branches. Two segments rather than one opaque string: a branch name
        // contains a slash the moment user branches exist, and a write route that
        // cannot name someone else's branch cannot write to it.
        var scoped = api.MapGroup("/projects/{project}/branches/{branch}");

        api.MapGet("/health", async (HttpContext context, ProjectRegistry projects) => {
            // Counts and errors are scoped the same way the lists are: a project
            // someone cannot see should not be visible to them as a number either.
            var visible = await context.VisibleProjectsAsync(projects);
            var all = projects.LoadAll();
            var result = new CatalogResult {
                Jobs = all.Jobs.Where(j => visible.ContainsKey(j.Project)).ToList(),
                Errors = all.Errors,
                Environments = all.Environments,
            };
            var pushes = projects.Projects
                .Where(p => visible.ContainsKey(p.Slug))
                .Select(p => new { Project = p, Git = projects.GitFor(p) })
                .Where(g => g.Git?.LastPush.At != null)
                .ToList();
            return Results.Ok(new {
                status = result.Errors.Count == 0 ? "ok" : "degraded",
                jobs = result.Jobs.Count,
                projects = visible.Count,
                notebooksRoot = projects.Default.Root,
                environments = projects.Environments,
                gitEnabled = projects.Projects.Any(p => visible.ContainsKey(p.Slug) && p.GitEnabled),
                // A push that can never succeed must not be silent divergence.
                lastPush = pushes.Count == 0 ? null : pushes.Select(g => new {
                    project = g.Project.Slug,
                    at = g.Git.LastPush.At,
                    ok = g.Git.LastPush.Ok,
                    error = g.Git.LastPush.Error,
                }),
                errors = result.Errors,
                version = typeof(JobsApi).Assembly.GetName().Version?.ToString(),
            });
        });

        // --- projects -------------------------------------------------------

        // Only what the caller may see. A project they have no grant on is not
        // listed, does not appear in the switcher, and 404s if they guess its id.
        api.MapGet("/projects", async (HttpContext context, ProjectRegistry projects) => {
            var visible = await context.VisibleProjectsAsync(projects);
            return Results.Ok(new {
                projects = projects.Projects
                    .Where(p => visible.ContainsKey(p.Slug))
                    .Select(p => ProjectView.From(p, projects, visible[p.Slug])),
            });
        });

        api.MapGet("/projects/{project}", async (
            HttpContext context, ProjectRegistry projects, string project) =>
            projects.Find(project) is { } found
                ? Results.Ok(ProjectView.From(
                    found, projects, await context.ProjectRoleAsync(found.Slug)))
                : NoProject(project)).RequiresProject(ProjectRole.ProjectViewer);

        api.MapPost("/projects", (ProjectRegistry projects, ProjectWrite write) => {
            if (write == null) {
                return Results.BadRequest(new { error = "A project needs a name and a folder." });
            }
            try {
                var created = projects.Register(write.ToProject(), out var createdRoot);
                return Results.Created(
                    $"/api/projects/{created.Slug}",
                    new { project = ProjectView.From(created, projects), createdRoot });
            } catch (ProjectRegistry.ProjectException e) {
                return Results.BadRequest(new { error = e.Message });
            }
        }).AdminOnly();

        api.MapPut("/projects/{project}", (
            ProjectRegistry projects, string project, ProjectWrite write) => {
                if (write == null) {
                    return Results.BadRequest(new { error = "Nothing to change." });
                }
                try {
                    var updated = projects.Update(project, write.ApplyTo);
                    return updated == null
                        ? NoProject(project)
                        : Results.Ok(ProjectView.From(updated, projects));
                } catch (ProjectRegistry.ProjectException e) {
                    return Results.BadRequest(new { error = e.Message });
                }
            }).RequiresProject(ProjectRole.ProjectAdmin);

        // --- who is in a project --------------------------------------------
        //
        // Managed by the project's own admins as well as by Server Admins: the
        // point of per-project grants is that a project can be run by people who
        // are nobody in particular server-wide.

        api.MapGet("/projects/{project}/members", async (
            ProjectRegistry projects, IAuthStore auth, string project) => {
                if (projects.Find(project) is not { } found) {
                    return NoProject(project);
                }
                var members = await auth.MembersOfAsync(found.Slug);
                var users = (await auth.ListUsersAsync()).ToDictionary(u => u.User.Id);
                return Results.Ok(new {
                    members = members
                        .Where(m => users.ContainsKey(m.UserId))
                        .Select(m => new {
                            userId = m.UserId,
                            displayName = users[m.UserId].User.DisplayName,
                            serverRole = users[m.UserId].User.Role.ToString(),
                            role = m.Role.ToString(),
                            m.CreatedAt,
                        }),
                    // Who could be added. Server Admins are already admins here and
                    // do not need a grant, so offering one would be a no-op row.
                    candidates = users.Values
                        .Where(u => u.User.Role != UserRole.ServerAdmin
                            && !members.Any(m => m.UserId == u.User.Id))
                        .Select(u => new { userId = u.User.Id, u.User.DisplayName }),
                });
            }).RequiresProject(ProjectRole.ProjectAdmin);

        api.MapPut("/projects/{project}/members/{userId:guid}", async (
            ProjectRegistry projects, IAuthStore auth, string project, Guid userId, MemberWrite write) => {
                if (projects.Find(project) is not { } found) {
                    return NoProject(project);
                }
                if (await auth.FindUserAsync(userId) == null) {
                    return Results.NotFound(new { error = "No such account." });
                }
                await auth.SetMemberAsync(found.Slug, userId, write?.Role ?? ProjectRole.ProjectViewer,
                    DateTime.UtcNow);
                return Results.Ok(new { granted = true });
            }).RequiresProject(ProjectRole.ProjectAdmin);

        api.MapDelete("/projects/{project}/members/{userId:guid}", async (
            ProjectRegistry projects, IAuthStore auth, string project, Guid userId) => {
                if (projects.Find(project) is not { } found) {
                    return NoProject(project);
                }
                return await auth.RemoveMemberAsync(found.Slug, userId)
                    ? Results.NoContent()
                    : Results.BadRequest(new {
                        error = "That is this project's last admin. Grant someone else first.",
                    });
            }).RequiresProject(ProjectRole.ProjectAdmin);

        // Turns a project's folder into a test/prod workspace — the same thing
        // `clrkernel-jobs git init` does, offered here because registering a project
        // from the browser and then being told to go and run a shell command is not
        // a workflow. Idempotent, and it adopts whatever is already in the folder.
        api.MapPost("/projects/{project}/init", (ProjectRegistry projects, string project) => {
            if (projects.Find(project) is not { } found) {
                return NoProject(project);
            }
            if (projects.GitFor(found) is not { } git) {
                return Results.BadRequest(new {
                    error = "This project does not use the test/prod workflow. Turn it on first.",
                });
            }
            try {
                return Results.Ok(new { message = git.Init() });
            } catch (GitException e) {
                return Results.BadRequest(new { error = e.Message });
            }
        }).RequiresProject(ProjectRole.ProjectAdmin);

        // Unregisters only. Nothing on disk is touched — see ProjectRegistry.
        api.MapDelete("/projects/{project}", (ProjectRegistry projects, string project) => {
            try {
                return projects.Unregister(project) ? Results.NoContent() : NoProject(project);
            } catch (ProjectRegistry.ProjectException e) {
                return Results.BadRequest(new { error = e.Message });
            }
        }).RequiresProject(ProjectRole.ProjectAdmin);

        // --- notebooks ------------------------------------------------------

        api.MapGet("/projects/{project}/notebooks", async (
            HttpContext context, ProjectRegistry projects, IAuthStore auth, string project) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                var catalog = scope.Catalog;
                var result = catalog.Load();
                // `label` is what a person reads and `name` is what a route says.
                // They differ for the branches that belong to somebody: `user-<id>`
                // is not a thing to show anyone.
                var trees = catalog.Environments.Select(env => new {
                    name = env,
                    label = env,
                    tree = Directory.Exists(catalog.RootFor(env))
                        ? NotebookTree.Build(catalog.RootFor(env), result, scope.Project.Slug, env)
                        : null,
                }).ToList();

                // Your own branch first: it is where editing happens, so a file list
                // that only offered test would be showing you the files you are not
                // the one changing.
                //
                // Made here rather than waited for. It used to appear only once a
                // worktree existed, and a worktree came into being on the first save
                // — so you could not save until you had picked your branch, and you
                // could not pick it until you had saved. A viewer still gets no
                // checkout: they can never write to one.
                if (scope.Git != null
                    && await context.ProjectRoleAsync(scope.Project.Slug) >= ProjectRole.ProjectMember
                    && context.CurrentUser() is { } me) {
                    // The one read that makes a branch, and it makes it here rather
                    // than in BranchFor: this is the page you open in order to have
                    // one, so it is the read that means you are about to work.
                    var mine = scope.Git.EnsureUserWorktree(me.Id);
                    ConnectionsApi.OnWorktreeCreated(context, scope.Git, me.Id);
                    // Annotated with this branch's jobs, which is none: the catalog
                    // scans environments, and a personal branch is not one. Leaving
                    // the environment out would annotate with every job on the
                    // server instead, which is a label that is simply untrue.
                    trees.Insert(0, new {
                        name = _mineBranch,
                        label = "My branch",
                        tree = NotebookTree.Build(mine, result, scope.Project.Slug, _mineBranch),
                    });
                }

                // Then everybody else's, which the branch switcher has always
                // offered and this list never did — so the one page for browsing
                // files was the one place another person's work was invisible.
                // Readable by anyone who may see the project, writable by nobody:
                // that rule is enforced on every write route, not here.
                //
                // ponytail: a tree built per person per request. Fine for a team;
                // key a cache on each worktree's HEAD if a big one ever drags.
                if (scope.Git != null) {
                    var caller = context.CurrentUser();
                    foreach (var user in (await auth.ListUsersAsync())
                                 .Where(u => caller == null || u.User.Id != caller.Id)
                                 .Where(u => scope.Git.HasUserWorktree(u.User.Id))
                                 .OrderBy(u => u.User.DisplayName, StringComparer.OrdinalIgnoreCase)) {
                        var theirs = GitService.BranchForUser(user.User.Id);
                        var named = _someoneBranch + user.User.Id.ToString("D");
                        trees.Add(new {
                            name = named,
                            label = user.User.DisplayName,
                            // Annotated with this branch's jobs, which is none —
                            // the same reason a personal branch carries no labels
                            // when it is your own.
                            tree = NotebookTree.Build(
                                scope.Git.PathFor(theirs), result, scope.Project.Slug, named),
                        });
                    }
                }
                return Results.Ok(new { environments = trees });
            }).RequiresProject(ProjectRole.ProjectViewer);

        scoped.MapGet("/notebooks/content", (
            HttpContext context, ProjectRegistry projects,
            string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (!Reachable(scope, branch)) {
                    return Results.NotFound(new { error = $"No branch '{branch}'." });
                }
                // Rooted at the branch's own tree — resolving against the workspace
                // would happily reach across into the other worktree.
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                if (resolved == null) {
                    return Results.BadRequest(new { error = "Path is outside the notebooks root." });
                }
                return File.Exists(resolved)
                    ? Results.Text(File.ReadAllText(resolved), "text/plain")
                    : Results.NotFound(new { error = $"No such file: {path}" });
            }).RequiresProject(ProjectRole.ProjectViewer);

        scoped.MapPut("/notebooks/content", async (
            ProjectRegistry projects, string project, string branch, string path,
            HttpContext context) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (EditableTarget(context, scope, branch, path) is not { } target) {
                    return TestWriteError(context, scope, branch, path);
                }
                if (context.Request.ContentLength is > 2_000_000) {
                    return Results.BadRequest(new { error = "File too large (2 MB limit)." });
                }
                using var reader = new StreamReader(context.Request.Body);
                return SaveToBranch(context, scope, branch, target, path, await reader.ReadToEndAsync());
            }).RequiresProject(ProjectRole.ProjectMember);

        // The notebook as editable cells, with the languages the kernel can run —
        // the shape the web editor works in. Parsing is NotebookMarkdown's, the same
        // reader/writer `clrkernel run` and the VS Code extension agree with.
        scoped.MapGet("/notebooks/cells", async (
            HttpContext context, ProjectRegistry projects, KernelLanguages kernelLanguages,
            string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (!Reachable(scope, branch)) {
                    return Results.NotFound(new { error = $"No branch '{branch}'." });
                }
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
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
            }).RequiresProject(ProjectRole.ProjectViewer);

        scoped.MapPut("/notebooks/cells", async (
            ProjectRegistry projects, KernelLanguages kernelLanguages,
            string project, string branch, string path, HttpContext context) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (EditableTarget(context, scope, branch, path) is not { } target) {
                    return TestWriteError(context, scope, branch, path);
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
                return SaveToBranch(
                    context, scope, branch, target, path,
                    NotebookMarkdown.Serialize(write.Cells.Select(c => c.ToCell(languages))));
            }).RequiresProject(ProjectRole.ProjectMember);

        // --- interactive sessions -------------------------------------------
        //
        // Running a cell executes code the request body carried, against a warm
        // kernel that outlives the request. Nothing here writes to the run store:
        // an interactive run leaves no Run rows, so it can never become the green
        // evidence promotion requires.

        scoped.MapPost("/notebooks/session", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options,
            NotebookSessionManager sessions, KernelLanguages kernelLanguages,
            string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                try {
                    // The session seeds kernelLanguages itself, on start and again
                    // whenever #r adds one — one place, so the two cannot drift.
                    var (key, ephemeral) = SessionFor(context, scope, branch, resolved);
                    var session = await sessions.GetOrStartAsync(
                        resolved, context.RequestAborted, key, ephemeral);
                    return Results.Ok(SessionView.From(session, false));
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message, kernelLog = sessions.Find(SessionFor(context, scope, branch, resolved).Key)?.KernelLog() });
                }
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapDelete("/notebooks/session", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options,
            NotebookSessionManager sessions, string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                return Results.Ok(new {
                    restarted = sessions.Restart(SessionFor(context, scope, branch, resolved).Key),
                });
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapPost("/notebooks/run", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options, IRunStore store,
            NotebookSessionManager sessions, string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
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
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                if (await DenyOverlap(scope, store, branch, resolved) is { } busy) {
                    return busy;
                }
                NotebookSession session;
                try {
                    var (key, ephemeral) = SessionFor(context, scope, branch, resolved);
                    session = await sessions.GetOrStartAsync(
                        resolved, context.RequestAborted, key, ephemeral);
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }

                var languages = session.Languages;
                var cells = request.Cells.Select(c => c.ToCell(languages)).ToList();
                var ids = request.Cells.Select((c, i) => c.Id ?? $"run{i}").ToList();
                // The run continues after the response: a long cell must not hold an
                // HTTP request open, and the editor polls status for progress.
                if (!session.TryStartRun(cells, ids, out var completion)) {
                    return Results.Json(
                        new { error = "This notebook is already running a cell." }, statusCode: 409);
                }
                // "Who ran that against production?" is the question somebody will
                // actually ask, so running in test or prod leaves a row saying so.
                // Your own branch does not: nothing there has happened to anyone else.
                await AuditAsync(context, store, scope, branch, path, ids, completion);
                return Results.Accepted(value: new { running = ids });
            }).RequiresProject(ProjectRole.ProjectMember);

        // What the editor currently has open, so completion and hover have documents
        // to answer about. Called on a debounce while typing, so it must stay cheap
        // and must never start a kernel: a broken configuration would otherwise
        // attempt a spawn every few hundred milliseconds for as long as someone types.
        // The editor starts its session when it opens the notebook.
        scoped.MapPost("/notebooks/sync", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options,
            NotebookSessionManager sessions, string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
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
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                var session = sessions.Find(SessionFor(context, scope, branch, resolved).Key);
                if (session == null) {
                    return Results.Ok(new { started = false, sent = 0 });
                }
                try {
                    return Results.Ok(new { started = true, sent = await session.SyncAsync(request.Cells, context.RequestAborted) });
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }
            }).RequiresProject(ProjectRole.ProjectMember);

        // One language question about one cell — completion, its lazy documentation,
        // hover, signature help. A whitelisted kind rather than four near-identical
        // routes, and an allowlist rather than a method proxy: completion evaluates
        // against a live REPL, so this is gated exactly like running a cell.
        //
        // The request carries the cell's current text and the session syncs it before
        // asking. The debounced sync is for siblings; a keystroke-triggered completion
        // cannot wait for it, and a position measured against text 300ms behind the
        // cursor answers with the wrong symbol rather than failing.
        scoped.MapPost("/notebooks/language", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options,
            NotebookSessionManager sessions, string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
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
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                var session = sessions.Find(SessionFor(context, scope, branch, resolved).Key);
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
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapGet("/notebooks/session/status", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options, IRunStore store,
            NotebookSessionManager sessions, string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
                    return denial;
                }
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                // A scheduled run of this notebook may be in flight in its own kernel;
                // the editor says so rather than leaving the file changing unexplained.
                // Checked before the session lookup, because the warning is most useful
                // when you open the file — which is before any kernel of yours exists.
                // ponytail: Load() re-reads the jobs yaml under the git lock, and the
                // editor polls this ~2.5×/s while a cell runs. Fine for one person's
                // handful of files; cache it per notebook if that ever shows up.
                var scheduled = false;
                foreach (var job in scope.Catalog.Load().In(scope.Project.Slug, branch).Where(j =>
                    string.Equals(j.NotebookPath, resolved, StringComparison.OrdinalIgnoreCase))) {
                    scheduled |= await store.HasActiveRunAsync(job.Project, job.Environment, job.Name);
                }
                var session = sessions.Find(SessionFor(context, scope, branch, resolved).Key);
                return session == null
                    ? Results.Ok(new { running = false, started = false, scheduledRunActive = scheduled })
                    : Results.Ok(SessionView.From(session, scheduled));
            }).RequiresProject(ProjectRole.ProjectViewer);

        // The connection wizard's schema. Answered by the notebook's own kernel so
        // a package `#r`-ed into this session contributes its providers too — the
        // browser never learns what a connection type is, it renders what it is told.
        scoped.MapGet("/notebooks/connections", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options,
            NotebookSessionManager sessions, string project, string branch,
            string path, string languageId) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (await DenyExecution(context, scope, branch, path) is { } denial) {
                    return denial;
                }
                if (string.IsNullOrWhiteSpace(languageId)) {
                    return Results.BadRequest(new { error = "languageId is required." });
                }
                var resolved = NotebookTree.SafeResolve(RootOf(scope, branch), path);
                try {
                    var (key, ephemeral) = SessionFor(context, scope, branch, resolved);
                    var session = await sessions.GetOrStartAsync(
                        resolved, context.RequestAborted, key, ephemeral);
                    var reply = await session.DescribeConnectionsAsync(languageId, context.RequestAborted);
                    return Results.Ok(new { providers = reply });
                } catch (Exception e) {
                    return Results.BadRequest(new { error = e.Message });
                }
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapGet("/notebooks/promotion", async (
            HttpContext context, ProjectRegistry projects, IRunStore store,
            string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (PromotionRefusal(scope, branch, path) is { } refusal) {
                    return refusal;
                }
                return Results.Ok(await Promotion.CheckAsync(scope.Project, projects, store, path));
            }).RequiresProject(ProjectRole.ProjectViewer);

        scoped.MapPost("/notebooks/promote", async (
            HttpContext context, ProjectRegistry projects, IRunStore store, JobsOptions options,
            string project, string branch, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                if (PromotionRefusal(scope, branch, path) is { } refusal) {
                    return refusal;
                }
                // Re-check inside the request: the button may be stale.
                var eligibility = await Promotion.CheckAsync(scope.Project, projects, store, path);
                if (!eligibility.Eligible) {
                    return Results.Conflict(new { error = "Not eligible.", reasons = eligibility.Reasons });
                }
                var sha = Promotion.Apply(scope.Git, eligibility, path);
                scope.Git.TryPush(scope.Project.Remote ?? options.GitPushRemote);
                return Results.Ok(new { promoted = true, commitSha = sha, paths = eligibility.Paths });
            }).RequiresProject(ProjectRole.ProjectAdmin);

        // --- your branch, and getting it into test ---------------------------

        // Who drove this notebook by hand, and what happened. Any Project Viewer may
        // read it: an audit nobody can see answers nothing.
        api.MapGet("/projects/{project}/manual-runs", async (
            ProjectRegistry projects, IRunStore store,
            string project, string branch, string path, int? limit) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                return Results.Ok(new {
                    runs = await store.QueryManualRunsAsync(new ManualRunQuery {
                        Project = scope.Project.Slug,
                        Environment = branch,
                        NotebookPath = path,
                        Limit = Clamp(limit),
                    }),
                });
            }).RequiresProject(ProjectRole.ProjectViewer);

        // --- personal worktrees, for whoever has to tidy up -------------------

        api.MapGet("/projects/{project}/worktrees", async (
            ProjectRegistry projects, IAuthStore auth, string project) => {
                if (Scope.Of(projects, project) is not { } scope || scope.Git == null) {
                    return Results.Ok(new { worktrees = Array.Empty<object>() });
                }
                var users = (await auth.ListUsersAsync()).ToDictionary(u => u.User.Id);
                return Results.Ok(new {
                    worktrees = scope.Git.UserWorktrees().Select(w => new {
                        userId = w.UserId,
                        owner = users.TryGetValue(w.UserId, out var user)
                            ? user.User.DisplayName
                            // The account is gone but the branch is still here, which
                            // is exactly the case this page exists to clean up.
                            : "(removed account)",
                        w.LastCommit,
                        w.Dirty,
                        w.Merged,
                    }),
                });
            }).RequiresProject(ProjectRole.ProjectAdmin);

        api.MapDelete("/projects/{project}/worktrees/{userId:guid}", (
            ProjectRegistry projects, string project, Guid userId, bool? force) => {
                if (Scope.Of(projects, project) is not { } scope || scope.Git == null) {
                    return Results.BadRequest(new { error = "The git workflow is not enabled." });
                }
                var refusal = scope.Git.RemoveUserWorktree(userId, force ?? false);
                // Deleting somebody's branch is a thing an admin may do; doing it
                // to unfinished work they have not shared takes saying so twice.
                return refusal == null
                    ? Results.NoContent()
                    : Results.Json(new { error = refusal, needsForce = true }, statusCode: 409);
            }).RequiresProject(ProjectRole.ProjectAdmin);

        // What branches this project has, and who owns each. The switcher renders
        // this; everything but your own is read-only, and says so here rather than
        // leaving the client to work it out from a name.
        api.MapGet("/projects/{project}/branches", async (
            HttpContext context, ProjectRegistry projects, IAuthStore auth, string project) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                var me = context.CurrentUser();
                var branches = new List<object>();
                if (scope.Git != null) {
                    if (me != null) {
                        branches.Add(new {
                            id = _mineBranch,
                            label = "My branch",
                            owner = me.DisplayName,
                            mine = true,
                            writable = true,
                        });
                    }
                    foreach (var user in (await auth.ListUsersAsync())
                                 .Where(u => me == null || u.User.Id != me.Id)
                                 .Where(u => scope.Git.HasUserWorktree(u.User.Id))
                                 .OrderBy(u => u.User.DisplayName, StringComparer.OrdinalIgnoreCase)) {
                        branches.Add(new {
                            id = _someoneBranch + user.User.Id.ToString("D"),
                            label = user.User.DisplayName,
                            owner = user.User.DisplayName,
                            mine = false,
                            writable = false,
                        });
                    }
                }
                foreach (var environment in scope.Catalog.Environments) {
                    branches.Add(new {
                        id = environment,
                        label = environment,
                        owner = (string)null,
                        mine = false,
                        writable = false,
                    });
                }
                return Results.Ok(new { branches });
            }).RequiresProject(ProjectRole.ProjectViewer);

        api.MapGet("/projects/{project}/branch", (
            HttpContext context, ProjectRegistry projects, string project) => {
                if (Scope.Of(projects, project) is not { } scope || scope.Git == null) {
                    return Results.Ok(new { hasBranch = false });
                }
                var user = context.CurrentUser();
                if (user == null || !scope.Git.HasUserWorktree(user.Id)) {
                    // No worktree yet is the normal state until the first edit, and
                    // asking about it must not be what creates one.
                    return Results.Ok(new { hasBranch = false });
                }
                var standing = scope.Git.StandingOf(user.Id);
                return Results.Ok(new {
                    hasBranch = true,
                    branch = GitService.BranchForUser(user.Id),
                    standing.Dirty,
                    standing.Ahead,
                    standing.Behind,
                    standing.Conflicts,
                });
            }).RequiresProject(ProjectRole.ProjectViewer);

        api.MapPost("/projects/{project}/branch/push", async (
            HttpContext context, ProjectRegistry projects, JobsOptions options, string project) => {
                if (Scope.Of(projects, project) is not { } scope || scope.Git == null) {
                    return Results.BadRequest(new { error = "The git workflow is not enabled." });
                }
                var user = context.CurrentUser();
                var message = (await BodyOf<PushWrite>(context))?.Message?.Trim();
                if (string.IsNullOrWhiteSpace(message)) {
                    message = $"changes from {user?.DisplayName}";
                }
                var result = scope.Git.PushToTest(user.Id, message, user?.DisplayName, EmailFor(user));
                if (!result.Pushed) {
                    return Results.Json(
                        new { error = result.Error, needsUpdate = result.NeedsUpdate },
                        statusCode: 409);
                }
                scope.Git.TryPush(scope.Project.Remote ?? options.GitPushRemote);
                return Results.Ok(new { pushed = true, commitSha = result.Sha });
            }).RequiresProject(ProjectRole.ProjectMember);

        api.MapPost("/projects/{project}/branch/update", (
            HttpContext context, ProjectRegistry projects, string project) => {
                if (Scope.Of(projects, project) is not { } scope || scope.Git == null) {
                    return Results.BadRequest(new { error = "The git workflow is not enabled." });
                }
                var user = context.CurrentUser();
                var conflicts = scope.Git.UpdateFromTest(user.Id, user?.DisplayName, EmailFor(user));
                // Conflicts come back as a list of files with markers left in them,
                // never as a resolution: taking one side automatically is how a merge
                // silently loses somebody's work.
                return Results.Ok(new { merged = conflicts.Count == 0, conflicts });
            }).RequiresProject(ProjectRole.ProjectMember);

        api.MapGet("/projects/{project}/git/diff", (
            ProjectRegistry projects, string project, string path) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                if (PromotionRefusal(scope, GitService.TestBranch, path) is { } refusal) {
                    return refusal;
                }
                return Results.Text(scope.Git.UnifiedDiff(path), "text/plain");
            }).RequiresProject(ProjectRole.ProjectViewer);

        // --- jobs -----------------------------------------------------------

        // Every project's jobs: the dashboard is a view of the whole server, and a
        // job carries the project it belongs to.
        api.MapGet("/jobs", async (HttpContext context, ProjectRegistry projects) => {
            var visible = await context.VisibleProjectsAsync(projects);
            var result = projects.LoadAll();
            var jobs = result.Jobs.Where(j => visible.ContainsKey(j.Project))
                .Select(JobView.From).ToList();

            // Jobs you have written but not pushed yet.
            //
            // They are deliberately not in LoadAll — the scheduler reads that, and a
            // personal branch is not something that runs. But leaving them out here
            // means a job you just made is missing from the page whose entire job is
            // to list your jobs, which reads as the save having failed.
            //
            // Only the ones test does not already have, by name: a job you are
            // editing is the same job, and showing both copies of it would double
            // the list for the sake of a badge. Against test specifically, not
            // against everything loaded — a prod-only leftover is not a reason to
            // hide the copy you are working on.
            if (context.CurrentUser() is { } user) {
                var mine = GitService.BranchForUser(user.Id);
                foreach (var project in projects.Projects) {
                    if (!visible.TryGetValue(project.Slug, out var role)
                        || role < ProjectRole.ProjectMember) {
                        continue;
                    }
                    // Asked, never ensured: this route spans every project and the
                    // dashboard polls it, so making the worktree here would be a
                    // checkout in every registered project on page load.
                    if (projects.GitFor(project) is not { } git || !git.HasUserWorktree(user.Id)) {
                        continue;
                    }
                    var inTest = result.Jobs
                        .Where(j => j.Project == project.Slug && j.Environment == GitService.TestBranch)
                        .Select(j => j.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    jobs.AddRange(projects.CatalogFor(project, mine).Load().Jobs
                        .Where(j => !inTest.Contains(j.Name))
                        .Select(JobView.From));
                }
            }
            return Results.Ok(new { jobs, errors = result.Errors });
        });

        scoped.MapGet("/jobs/{name}", (
            HttpContext context, ProjectRegistry projects,
            string project, string branch, string name) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                var job = scope.CatalogFor(branch).Load()
                    .Find(scope.Project.Slug, scope.EnvironmentOf(branch), name);
                return job == null ? Results.NotFound(new { error = $"No job named '{name}' in {branch}." })
                    : Results.Ok(JobView.From(job));
            }).RequiresProject(ProjectRole.ProjectViewer);

        scoped.MapPost("/jobs", (
            HttpContext context, ProjectRegistry projects,
            string project, string branch, JobWrite write) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                return Upsert(context, scope, scope.BranchFor(context, branch), null, write);
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapPut("/jobs/{name}", (
            HttpContext context, ProjectRegistry projects,
            string project, string branch, string name, JobWrite write) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                return Upsert(context, scope, scope.BranchFor(context, branch), name, write);
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapDelete("/jobs/{name}", (
            HttpContext context, ProjectRegistry projects,
            string project, string branch, string name) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                // Deleting a job is editing a jobs file, so it happens where every
                // other edit does: your own branch. It used to look for the job in
                // the project's own catalog and commit the removal in test's
                // worktree — which found nothing on a personal branch, and would
                // have landed a commit on test that nobody pushed.
                if (scope.Catalog.GitLayout && !scope.OwnedBy(context, branch)) {
                    return Results.BadRequest(new {
                        error = GitService.IsUserBranch(branch)
                            ? "That is somebody else's branch."
                            : "test and prod are read-only. Delete on your own branch and push to test.",
                    });
                }
                var catalog = scope.CatalogFor(branch);
                var job = catalog.Load().Find(scope.Project.Slug, scope.EnvironmentOf(branch), name);
                if (job == null) {
                    return Results.NotFound(new { error = $"No job named '{name}'." });
                }
                void Mutate() {
                    var file = JobsFile.Read(job.SourceFile);
                    file.Jobs.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (file.Jobs.Count == 0) {
                        // A jobs file with an empty list can't be loaded; remove it instead.
                        File.Delete(job.SourceFile);
                    } else {
                        JobsFile.Write(job.SourceFile, file, catalog.NotebooksRoot);
                    }
                    // A file write, not a commit — the rule Upsert and the notebook
                    // editor both follow. Push to test is where a deletion becomes
                    // history.
                }
                if (scope.Git != null && scope.Catalog.GitLayout) {
                    scope.Git.WithLock(Mutate);
                } else {
                    Mutate();
                }
                return Results.NoContent();
            }).RequiresProject(ProjectRole.ProjectMember);

        // The body is read by hand rather than bound: a [FromBody] parameter adds a
        // content-type constraint to route matching, which makes a plain
        // `curl -X POST …/run` (no body, no headers) miss the route entirely.
        scoped.MapPost("/jobs/{name}/run", async (
            HttpContext context, ProjectRegistry projects, SchedulerService scheduler,
            string project, string branch, string name) => {
                if (Scope.Of(projects, project) is not { } scope) {
                    return NoProject(project);
                }
                branch = scope.BranchFor(context, branch);
                // Jobs run in test and prod; a personal branch is where you write
                // them. Cells are how you try one out before it goes anywhere.
                if (GitService.IsUserBranch(branch)) {
                    return Results.BadRequest(new {
                        error = "Jobs run in test and prod. Push this to test, then run it there.",
                    });
                }
                var job = scope.Catalog.Load().Find(scope.Project.Slug, branch, name);
                if (job == null) {
                    return Results.NotFound(new { error = $"No job named '{name}' in {branch}." });
                }

                var overrides = await BodyOf<RunOverrides>(context);

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
            }).RequiresProject(ProjectRole.ProjectMember);

        scoped.MapPost("/jobs/{name}/cancel", (
            HttpContext context, ProjectRegistry projects, SchedulerService scheduler,
            string project, string branch, string name) =>
            projects.Find(project) is not { } found
                ? NoProject(project)
                : scheduler.TryCancel(found.Slug, branch, name)
                    ? Results.Ok(new { cancelled = true })
                    : Results.NotFound(new { error = $"No in-flight run for '{name}' in {branch}." }))
            .RequiresProject(ProjectRole.ProjectMember);

        scoped.MapGet("/jobs/{name}/runs", async (
            HttpContext context, ProjectRegistry projects, IRunStore store,
            string project, string branch, string name, int? limit, int? offset) =>
            projects.Find(project) is not { } found
                ? NoProject(project)
                : Results.Ok(await store.QueryRunsAsync(new RunQuery {
                    Project = found.Slug,
                    Environment = branch,
                    JobName = name,
                    Limit = Clamp(limit),
                    Offset = offset ?? 0,
                }))).RequiresProject(ProjectRole.ProjectViewer);

        // --- runs -----------------------------------------------------------

        // What a cron actually does, answered by the same Cronos the scheduler and the
        // save path use. One source of truth, so the field cannot accept an
        // expression the save will refuse — and cannot read it differently either.
        //
        // Next occurrences rather than a sentence of English: "0 2 * * *" described
        // as "at 02:00 every day" still leaves the reader to work out whose 02:00,
        // and a list of instants answers that by being one.
        api.MapGet("/cron/preview", (string expression, int? count) => {
            if (string.IsNullOrWhiteSpace(expression)) {
                return Results.Ok(new { valid = false, error = (string)null, next = new List<string>() });
            }
            Cronos.CronExpression parsed;
            try {
                parsed = Cronos.CronExpression.Parse(expression.Trim());
            } catch (Cronos.CronFormatException e) {
                return Results.Ok(new { valid = false, error = e.Message, next = new List<string>() });
            }
            // UTC, because UTC is what the scheduler compares against — see
            // SchedulerService.IsDue. Reading these as local time is how the nightly
            // close gets scheduled for the wrong hour, so the client says so.
            var from = DateTime.UtcNow;
            var next = new List<string>();
            for (var i = 0; i < Math.Clamp(count ?? 5, 1, 10); i++) {
                if (parsed.GetNextOccurrence(from, inclusive: false) is not { } occurrence) {
                    break;
                }
                next.Add(occurrence.ToString("o"));
                from = occurrence;
            }
            return Results.Ok(new { valid = true, error = (string)null, next });
        });

        api.MapGet("/runs", async (
            HttpContext context, ProjectRegistry projects, IRunStore store,
            string status, string project, string env, int? limit, int? offset) => {
                var visible = await context.VisibleProjectsAsync(projects);
                if (project != null && !visible.ContainsKey(project)) {
                    return NoProject(project);
                }
                RunStatus? parsed = null;
                if (!string.IsNullOrEmpty(status)) {
                    if (!Enum.TryParse<RunStatus>(status, ignoreCase: true, out var value)) {
                        return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                    }
                    parsed = value;
                }
                var runs = await store.QueryRunsAsync(new RunQuery {
                    Project = project,
                    Environment = env,
                    Status = parsed,
                    Limit = Clamp(limit),
                    Offset = offset ?? 0,
                });
                // Named one project: checked above. Named none: the history of a
                // project you cannot see is part of what you cannot see.
                return Results.Ok(project != null
                    ? runs
                    : runs.Where(r => visible.ContainsKey(r.Project ?? ProjectRegistry.DefaultSlug))
                        .ToList());
            });

        // A run is part of its project, and so is the fact that it happened: these
        // three answer 404 for a run in a project the caller cannot see, which is
        // the same thing they answer for a run id that never existed.
        api.MapGet("/runs/{id:guid}", async (
            HttpContext context, ProjectRegistry projects, IRunStore store, Guid id) => {
                var run = await store.GetRunAsync(id);
                return await VisibleRun(context, projects, run) == null
                    ? Results.NotFound(new { error = $"No run {id}." })
                    : Results.Ok(new { run, cells = await store.GetCellsAsync(id) });
            });

        api.MapGet("/runs/{id:guid}/artifact", async (
            HttpContext context, ProjectRegistry projects, IRunStore store, JobsOptions options, Guid id) =>
            await ServeRunFile(context, projects, store, options, id, r => r.ArtifactPath, "application/json"));

        api.MapGet("/runs/{id:guid}/log", async (
            HttpContext context, ProjectRegistry projects, IRunStore store, JobsOptions options, Guid id) =>
            await ServeRunFile(context, projects, store, options, id, r => r.LogPath, "text/plain"));

        api.MapGet("/stats", async (
            HttpContext context, ProjectRegistry projects, IRunStore store, int? days) =>
            Results.Ok(await store.GetStatsAsync(
                TimeSpan.FromDays(Math.Clamp(days ?? 7, 1, 365)),
                (await context.VisibleProjectsAsync(projects)).Keys.ToList())));

        // --- notification channels -------------------------------------------

        // Channels are server-wide, not per project: notifications.yaml lives beside
        // the configured notebooks root and the Notifier reads it from there for
        // every run, whichever project produced it.
        api.MapGet("/channels", (JobsOptions options) => {
            var channels = NotificationChannels.Load(options.NotebooksRoot);
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

        api.MapPut("/channels", (JobsOptions options, NotificationChannels channels) => {
            if (channels?.Channels == null) {
                return Results.BadRequest(new { error = "Expected a channels list." });
            }
            try {
                NotificationChannels.Save(options.NotebooksRoot, channels);
            } catch (InvalidDataException e) {
                return Results.BadRequest(new { error = e.Message });
            }
            return Results.Ok(new { channels = channels.Channels.Count });
        }).AdminOnly();

        api.MapPost("/channels/{name}/test", async (JobsOptions options, Notifier notifier, string name) => {
            var channel = NotificationChannels.Load(options.NotebooksRoot).Find(name);
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
        }).AdminOnly();

        // --- settings ---------------------------------------------------------

        api.MapGet("/settings", (SettingsRegistry registry) =>
            Results.Ok(new { sections = registry.Sections }));

        api.MapPut("/settings/{section}", (SettingsRegistry registry, string section,
            Dictionary<string, JsonElement> values) => {
                var error = registry.Write(section, values);
                return error == null
                    ? Results.Ok(new { saved = true, restartRequired = true })
                    : Results.BadRequest(new { error });
            }).AdminOnly();

        // A mistyped API route must answer 404 JSON, not fall through to the SPA's
        // index.html fallback (which would hand a client 200 text/html).
        api.MapFallback(() => Results.NotFound(new { error = "No such API endpoint." }));
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? 50, 1, 500);

    /// <summary>
    /// A slug nobody registered is 404, never 403: a project you have no access to
    /// must be indistinguishable from a project that does not exist, or the list of
    /// project names leaks to anyone willing to guess.
    /// </summary>
    private static IResult NoProject(string slug) =>
        Results.NotFound(new { error = $"No project '{slug}'." });

    /// <summary>
    /// One project, resolved once per request: the catalog that scans it and the git
    /// layer that owns its workspace lock. Everything scoped to a project takes this
    /// rather than three parameters that could disagree.
    /// </summary>
    private sealed record Scope(
        Project Project, JobCatalog Catalog, GitService Git, ProjectRegistry Registry) {

        /// <summary>The catalog that knows this branch's jobs.</summary>
        public JobCatalog CatalogFor(string branch) => Registry.CatalogFor(Project, branch);

        /// <summary>How a branch names its environment to the catalog and the client.</summary>
        public string EnvironmentOf(string branch) =>
            GitService.IsUserBranch(branch) ? ProjectRegistry.MineEnvironment : branch;

        public static Scope Of(ProjectRegistry projects, string slug) =>
            projects.Find(slug) is { } project
                ? new Scope(project, projects.CatalogFor(project), projects.GitFor(project), projects)
                : null;

        /// <summary>
        /// The branch a route names, as git knows it. <c>mine</c> is the caller's own
        /// personal branch, and it is the <em>only</em> way to name a personal branch:
        /// there is no spelling of the request that reaches someone else's, so
        /// "nobody edits another user's branch" is a property of the route table
        /// rather than a check somebody has to remember to write.
        /// <para>
        /// Resolving it creates the worktree if this is their first edit here — but
        /// only for a request that is about to write. A read of a branch that is not
        /// there answers "no such file", which is true; making a checkout for
        /// somebody who is only looking means a viewer, who may never write
        /// anywhere, accumulates an empty branch in every project they open. Every
        /// route in a project that is not a GET needs Project Member or better, so
        /// "a viewer has no branch" is a property of this line and the route table
        /// together.
        /// </para>
        /// <para>
        /// Lazy at all because most people never touch most projects, and an empty
        /// checkout per person per project is a lot of disk to keep against that.
        /// </para>
        /// </summary>
        public string BranchFor(HttpContext context, string branch) {
            if (Git == null) {
                return branch;
            }
            if (branch == _mineBranch) {
                if (context.CurrentUser() is not { } user) {
                    return null;
                }
                if (!HttpMethods.IsGet(context.Request.Method)) {
                    Git.EnsureUserWorktree(user.Id);
                    ConnectionsApi.OnWorktreeCreated(context, Git, user.Id);
                }
                return GitService.BranchForUser(user.Id);
            }
            // Somebody else's, named as `user-<id>` because a route segment cannot
            // hold the slash the branch has. Never created here: a worktree comes
            // into being when its owner first edits, and only then.
            if (branch.StartsWith(_someoneBranch, StringComparison.Ordinal)
                && Guid.TryParse(branch[_someoneBranch.Length..], out var owner)) {
                return Git.HasUserWorktree(owner) ? GitService.BranchForUser(owner) : null;
            }
            return branch;
        }

        /// <summary>
        /// Whether the caller may write here. Their own branch, and nothing else.
        /// <para>
        /// It used to be enough that the branch was <em>a</em> personal one, because
        /// <c>mine</c> was the only way to name one. Reading somebody else's ended
        /// that, so the ownership rule is a check again — and it is the one rule
        /// nobody overrides: a Project Admin can delete a stale branch, never write
        /// into it.
        /// </para>
        /// </summary>
        public bool OwnedBy(HttpContext context, string branch) =>
            context.CurrentUser() is { } user
            && branch == GitService.BranchForUser(user.Id);
    }

    /// <summary>What a route calls the caller's own branch.</summary>
    private const string _mineBranch = "mine";

    /// <summary>And what it calls somebody else's, which is read-only to everyone.</summary>
    private const string _someoneBranch = "user-";

    /// <summary>
    /// A branch this project actually has: its environments, or the caller's own.
    /// <para>
    /// Environments alone is not the answer any more — that list is <c>test</c> and
    /// <c>prod</c>, which is what <em>runs</em>, and reading a notebook you are
    /// editing means reading a branch that is on neither list.
    /// </para>
    /// </summary>
    /// <summary>
    /// Where a branch's files are. The catalog places environments; a personal
    /// branch has a worktree of its own that the catalog never scans.
    /// </summary>
    private static string RootOf(Scope scope, string branch) =>
        GitService.IsUserBranch(branch) ? scope.Git.PathFor(branch) : scope.Catalog.RootFor(branch);

    private static bool Reachable(Scope scope, string branch) =>
        scope.Catalog.Environments.Contains(branch)
        || (GitService.IsUserBranch(branch) && scope.Git != null);

    /// <summary>Shared refusal for promotion and diff: both compare test against prod.</summary>
    private static IResult PromotionRefusal(Scope scope, string branch, string path) {
        if (scope.Git == null || !scope.Catalog.GitLayout) {
            return Results.BadRequest(new { error = "The git workflow is not enabled." });
        }
        if (branch != GitService.TestBranch) {
            return Results.BadRequest(new { error = "Promotion runs from test, not from prod." });
        }
        return NotebookTree.SafeResolve(scope.Catalog.RootFor(GitService.TestBranch), path) == null
            ? Results.BadRequest(new { error = "Path is outside the test area." })
            : null;
    }

    /// <summary>
    /// The absolute path a test write may target, or null when it may not — the git
    /// workflow is off, the branch is not test, the path escapes the test
    /// worktree, or the file is not one we edit. <see cref="TestWriteError"/> says
    /// which, so the two shapes of write (raw text, cells) refuse identically.
    /// </summary>
    private static string EditableTarget(
        HttpContext context, Scope scope, string branch, string path) {
        // Your own branch, and nothing else. test and prod are runnable but never
        // writable by anybody, and neither is anyone else's branch — this is a check
        // on which branch it is rather than on the caller's role, so no role can
        // satisfy it.
        if (!scope.Catalog.GitLayout || scope.Git == null || !scope.OwnedBy(context, branch)) {
            return null;
        }
        // Rooted at that worktree: the workspace root would resolve prod/… too.
        var resolved = NotebookTree.SafeResolve(scope.Git.PathFor(branch), path);
        if (resolved == null) {
            return null;
        }
        return NotebookTree.IsNotebook(resolved) || resolved.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase)
            ? resolved
            : null;
    }

    private static IResult TestWriteError(
        HttpContext context, Scope scope, string branch, string path) {
        if (!scope.Catalog.GitLayout || scope.Git == null) {
            return Results.BadRequest(new {
                error = "Editing needs the git workflow — run `clrkernel-jobs git init`.",
            });
        }
        if (!scope.OwnedBy(context, branch)) {
            return Results.BadRequest(new {
                error = GitService.IsUserBranch(branch)
                    ? "That is somebody else's branch. Nobody writes to another person's work."
                    : "test and prod are read-only. Edit on your own branch and push to test.",
            });
        }
        return NotebookTree.SafeResolve(scope.Git.PathFor(branch), path) == null
            ? Results.BadRequest(new { error = "Path is outside your branch." })
            : Results.BadRequest(new { error = "Only notebooks and *.jobs.yaml are editable here." });
    }

    /// <summary>
    /// The single policy check for every endpoint that executes code. Running a
    /// cell is the one place the tool runs code straight from a request body, so
    /// the decision lives in exactly one function.
    /// <para>
    /// The gate: the git workflow must be on, the environment must be test (prod is
    /// read-only and is not a scratchpad), the path must resolve inside the test
    /// worktree.
    /// </para>
    /// <para>
    /// It used to carry a fourth clause: refuse to execute when the server was
    /// bound beyond localhost with no API key, because that combination was remote
    /// code execution for anyone who could reach the port. That clause is gone
    /// because the condition cannot arise any more — every route here is
    /// <c>.AdminOnly()</c>, so reaching this code at all means a signed-in Server
    /// Admin. The check moved; it was not dropped.
    /// </para>
    /// </summary>
    private static async Task<IResult> DenyExecution(
        HttpContext context, Scope scope, string branch, string path) {
        if (!scope.Catalog.GitLayout) {
            return Results.BadRequest(new {
                error = "Running cells needs the git workflow — run `clrkernel-jobs git init`.",
            });
        }
        // Three places code may run, and they are not the same permission.
        //
        // Your own branch is where you work. test and prod are read-only but still
        // *runnable*: when a scheduled job dies at cell seven of twelve at two in the
        // morning, the fix is to run the rest, not to edit production. Somebody
        // else's branch is neither — the kernel would be working inside their files.
        if (!scope.OwnedBy(context, branch)) {
            if (GitService.IsUserBranch(branch)) {
                return Results.BadRequest(new {
                    error = "That is somebody else's branch. Open this notebook on yours.",
                });
            }
            if (branch == "prod" && await context.ProjectRoleAsync(scope.Project.Slug)
                    is not ProjectRole.ProjectAdmin) {
                return Results.Json(
                    new { error = "Only this project's admins run anything in production." },
                    statusCode: 403);
            }
            if (branch != "prod" && branch != GitService.TestBranch) {
                return Results.NotFound(new { error = $"No branch '{branch}'." });
            }
        }
        if (NotebookTree.SafeResolve(RootOf(scope, branch), path) == null) {
            return Results.BadRequest(new { error = "Path is outside this branch." });
        }
        return null;
    }

    /// <summary>
    /// Which session a run belongs to. The notebook's path when it is your own
    /// branch, so re-opening the editor finds the kernel you left; the person as
    /// well when it is test or prod, because two people hand-running the same
    /// production notebook sharing one kernel is not a thing anybody wants.
    /// </summary>
    private static (string Key, bool Ephemeral) SessionFor(
        HttpContext context, Scope scope, string branch, string resolved) =>
        scope.OwnedBy(context, branch)
            ? (resolved, false)
            : ($"{resolved}\u0000{context.CurrentUser()?.Id:D}", true);

    /// <summary>
    /// Opens an audit row for a hand-driven run in test or prod, and closes it when
    /// the batch finishes. Nothing is awaited but the open: the run outlives the
    /// request, which is the whole reason the row has to be closed by a continuation
    /// rather than by whoever polls next.
    /// </summary>
    private static async Task AuditAsync(
        HttpContext context, IRunStore store, Scope scope, string branch, string path,
        IReadOnlyList<string> ids, Task completion) {
        if (branch != GitService.TestBranch && branch != "prod") {
            return;
        }
        var user = context.CurrentUser();
        var record = new ManualRun {
            Id = Guid.NewGuid(),
            Project = scope.Project.Slug,
            Environment = branch,
            NotebookPath = path,
            ActorId = user?.Id ?? Guid.Empty,
            ActorName = user?.DisplayName,
            StartedAt = DateTime.UtcNow,
            Cells = string.Join(",", ids),
            CellCount = ids.Count,
        };
        await store.StartManualRunAsync(record);
        _ = completion.ContinueWith(
            finished => store.FinishManualRunAsync(
                record.Id,
                finished.IsFaulted ? "Failed" : "Succeeded",
                finished.Exception?.GetBaseException().Message,
                DateTime.UtcNow),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Refuses a hand-driven run while the schedule is running the same notebook.
    /// Two kernels in one worktree write over each other's outputs, and the one that
    /// matters is the one nobody is watching.
    /// </summary>
    private static async Task<IResult> DenyOverlap(
        Scope scope, IRunStore store, string branch, string resolved) {
        if (branch != GitService.TestBranch && branch != "prod") {
            return null;
        }
        foreach (var job in scope.Catalog.Load().In(scope.Project.Slug, branch).Where(j =>
                     string.Equals(j.NotebookPath, resolved, StringComparison.OrdinalIgnoreCase))) {
            if (await store.HasActiveRunAsync(job.Project, job.Environment, job.Name)) {
                return Results.Json(
                    new { error = $"'{job.Name}' is running this notebook right now. Wait for it." },
                    statusCode: 409);
            }
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
    /// <summary>
    /// Writes into the caller's own worktree. A <em>file write, not a commit</em>:
    /// the commit moment is pushing to test, where a person says what the batch of
    /// work was for.
    /// <para>
    /// Saving used to commit because test was the only branch there was, and an
    /// uncommitted test worktree is a scheduled run picking up half an edit. On a
    /// personal branch nothing else reads the files, so a commit per save would only
    /// bury the one message anybody wrote under a hundred nobody did.
    /// </para>
    /// </summary>
    private static IResult SaveToBranch(
        HttpContext context, Scope scope, string branch, string resolved, string path, string content) {
        scope.Git.WithLock(() => {
            var directory = Path.GetDirectoryName(resolved)!;
            Directory.CreateDirectory(directory);
            // Write beside it and rename over the top. The editor autosaves every
            // few seconds, so "crashed halfway through writing" stops being a
            // thought experiment — and a half-written notebook is not a notebook.
            // The staging file is in the same directory on purpose: a rename is
            // only atomic within one filesystem.
            var staging = Path.Combine(directory, "." + Path.GetFileName(resolved) + ".saving");
            File.WriteAllText(staging, content);
            File.Move(staging, resolved, overwrite: true);
        });
        return Results.Ok(new { saved = true, branch });
    }

    /// <summary>
    /// An optional JSON body, or null when there was none. Read by hand rather than
    /// bound, for the same reason the run route does: a [FromBody] parameter adds a
    /// content-type constraint to route matching, and a bodyless POST then misses
    /// the route entirely.
    /// <para>
    /// It does not consult Content-Length. A chunked request has none — HttpClient
    /// sends one for anything it did not buffer — and gating on it means silently
    /// ignoring a body that is right there.
    /// </para>
    /// </summary>
    private static async Task<T> BodyOf<T>(HttpContext context) where T : class {
        if (context.Request.ContentLength == 0) {
            return null;
        }
        try {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, _bodyJson);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// A git author address for an account. There are no email addresses in this
    /// system — passkeys need none — so this is a stable synthetic one rather than a
    /// claim about how to reach anybody.
    /// </summary>
    private static string EmailFor(User user) =>
        user == null ? null : $"{user.Id:D}@users.clrkernel.local";

    /// <summary>The run, or null when it does not exist or is not the caller's to see.</summary>
    private static async Task<Run> VisibleRun(
        HttpContext context, ProjectRegistry projects, Run run) =>
        run != null
        && (await context.VisibleProjectsAsync(projects))
            .ContainsKey(run.Project ?? ProjectRegistry.DefaultSlug)
            ? run
            : null;

    private static async Task<IResult> ServeRunFile(
        HttpContext context, ProjectRegistry projects, IRunStore store, JobsOptions options,
        Guid id, Func<Run, string> select, string contentType) {
        var run = await VisibleRun(context, projects, await store.GetRunAsync(id));
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

    /// <summary>
    /// Creating and editing jobs, on your own branch like everything else you edit.
    /// The yaml is a file in your worktree, and it reaches what runs by being
    /// pushed to test.
    /// </summary>
    private static IResult Upsert(
        HttpContext context, Scope scope, string branch, string existingName, JobWrite write) {
        var git = scope.Git;
        if (scope.Catalog.GitLayout && !scope.OwnedBy(context, branch)) {
            return Results.BadRequest(new {
                error = GitService.IsUserBranch(branch)
                    ? "That is somebody else's branch."
                    : "test and prod are read-only. Edit on your own branch and push to test.",
            });
        }
        var catalog = scope.CatalogFor(branch);
        var environment = scope.EnvironmentOf(branch);
        if (!catalog.Environments.Contains(environment)) {
            return Results.NotFound(new { error = $"No branch '{branch}'." });
        }
        if (string.IsNullOrWhiteSpace(write?.Name)) {
            return Results.BadRequest(new { error = "A job needs a name." });
        }
        if (string.IsNullOrWhiteSpace(write.Notebook)) {
            return Results.BadRequest(new { error = "A job needs a notebook path." });
        }

        var catalogResult = catalog.Load();
        var existing = existingName != null
            ? catalogResult.Find(scope.Project.Slug, environment, existingName)
            : null;
        if (existingName != null && existing == null) {
            return Results.NotFound(new { error = $"No job named '{existingName}' in {branch}." });
        }
        var clash = catalogResult.Find(scope.Project.Slug, environment, write.Name);
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
            if (git != null) {
                // A file write, not a commit — the same rule the notebook editor
                // follows. Push to test is where either of them becomes history.
                git.WithLock(() => JobsFile.Write(targetFile, file, catalog.NotebooksRoot));
            } else {
                JobsFile.Write(targetFile, file, catalog.NotebooksRoot);
            }
        } catch (Exception e) {
            return Results.BadRequest(new { error = $"Job is not valid: {e.Message}" });
        }

        var saved = catalog.Load().Find(scope.Project.Slug, environment, write.Name);
        return existing == null
            ? Results.Created(
                $"/api/projects/{scope.Project.Slug}/branches/{branch}/jobs/{write.Name}",
                JobView.From(saved))
            : Results.Ok(JobView.From(saved));
    }
}

/// <summary>The commit message a push carries.</summary>
public sealed class PushWrite {
    public string Message { get; set; }
}

/// <summary>One grant, as the members API sets it.</summary>
public sealed class MemberWrite {
    public ProjectRole Role { get; set; }
}

/// <summary>
/// A project as a registration or an edit describes it. Both use one shape, and the
/// fields an edit may not change are ignored rather than rejected: the slug is
/// written into every run row and the root is where that history happened.
/// </summary>
public sealed class ProjectWrite {
    public string Slug { get; set; }
    public string Name { get; set; }
    public string Root { get; set; }
    public bool GitEnabled { get; set; }
    public RemoteMode RemoteMode { get; set; } = RemoteMode.Local;
    public string Remote { get; set; }
    /// <summary>The name of a secret, never a credential. See <see cref="Project.RemoteSecret"/>.</summary>
    public string RemoteSecret { get; set; }
    public bool PushUserBranches { get; set; }

    public Project ToProject() {
        var project = new Project { Slug = Slug, Name = Name, Root = Root };
        ApplyTo(project);
        return project;
    }

    public void ApplyTo(Project project) {
        project.Name = Name ?? project.Name;
        project.GitEnabled = GitEnabled;
        project.RemoteMode = RemoteMode;
        project.Remote = string.IsNullOrWhiteSpace(Remote) ? null : Remote.Trim();
        project.RemoteSecret = string.IsNullOrWhiteSpace(RemoteSecret) ? null : RemoteSecret.Trim();
        project.PushUserBranches = PushUserBranches;
    }
}

/// <summary>A project as the API returns it. No credential ever appears here.</summary>
public sealed class ProjectView {
    public string Slug { get; set; }
    public string Name { get; set; }
    /// <summary>Where it lives. Admins configure this; it is not a secret.</summary>
    public string Root { get; set; }
    public bool GitEnabled { get; set; }
    /// <summary>False when git is on but `git init` has not been run on the folder yet.</summary>
    public bool Ready { get; set; }
    public string RemoteMode { get; set; }
    public string Remote { get; set; }
    /// <summary>The *name* of the secret holding the remote's credential, never the credential.</summary>
    public string RemoteSecret { get; set; }
    public bool PushUserBranches { get; set; }
    public IReadOnlyList<string> Environments { get; set; }

    /// <summary>What the caller may do here. Never null on a project they can see.</summary>
    public string Role { get; set; }

    public static ProjectView From(
        Project project, ProjectRegistry projects, ProjectRole? role = null) => new() {
            Role = role?.ToString(),
            Slug = project.Slug,
            Name = project.Name,
            Root = project.Root,
            GitEnabled = project.GitEnabled,
            Ready = !project.GitEnabled || projects.GitFor(project)?.LayoutExists == true,
            RemoteMode = project.RemoteMode.ToString(),
            Remote = project.Remote,
            RemoteSecret = project.RemoteSecret,
            PushUserBranches = project.PushUserBranches,
            Environments = projects.CatalogFor(project).Environments,
        };
}

/// <summary>A job as the API returns it (absolute paths stay server-side).</summary>
public sealed class JobView {
    public string Project { get; set; }
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
        Project = job.Project,
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

    /// <summary>The whole payload for the kinds that ask about something rather than
    /// about a position: the completion item for <c>resolve</c>, <c>{key}</c> for
    /// <c>metadataSource</c>. Forwarded verbatim — the server produced it.</summary>
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
