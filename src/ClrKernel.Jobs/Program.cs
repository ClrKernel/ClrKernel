using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

/// <summary>
/// The <c>clrkernel-jobs</c> command-line tool. Phase 1 verbs: <c>run</c> (one job,
/// step-by-step progress), <c>list</c>, <c>validate</c>. The scheduler host
/// (<c>serve</c>) and the web API arrive in later phases.
/// </summary>
public static class Program {
    private const string _usage =
        """
        clrkernel-jobs — notebook job runner for ClrKernel (preview).

        Usage: clrkernel-jobs <command> [options]

        Commands:
          serve           Run the scheduler: cron jobs fire on time, dependent jobs
                          fire when everything they need has freshly succeeded.
          run <job>       Run one job now and print per-cell progress.
                          With the git workflow: --env test (default) or prod.
          list            List the jobs found under the notebooks root.
          validate        Parse and validate every *.jobs.yaml; exit 1 on problems.
          git init        Turn the notebooks root into a test/prod git workspace
                          (adopts existing notebooks into test and promotes them).

        Options:
          --notebooks <dir>          Notebooks root (default: current directory,
                                     or CLRKERNEL_JOBS_NOTEBOOKS).
          --data-dir <dir>           Data directory (default: ~/.clrkernel/jobs,
                                     or CLRKERNEL_JOBS_DATA).
          --clrkernel <path>         Path to the clrkernel executable (default:
                                     PATH, then ~/.dotnet/tools).
          --store <kind>             Run-history store: sqlite | sqlserver |
                                     postgres | files. Required for serve;
                                     one-shot commands default to sqlite.
          --connection-string <cs>   Connection string for sqlserver/postgres.
          --urls <urls>              serve: listen address
                                     (default http://localhost:5000).
          --rp-id <domain>           serve: the domain passkeys are bound to
                                     (or CLRKERNEL_JOBS_RPID). Default localhost.
          --origins <url;url>        serve: origins the browser may present
                                     (or CLRKERNEL_JOBS_ORIGINS). Default: --urls.
          --max-parallelism <n>      serve: concurrent runs (default 4).
          --git <true|false>         Enable the test/prod git workflow
                                     (or CLRKERNEL_JOBS_GIT).
          --env <test|prod>          run: which environment (default test).
          -h, --help                 Show this help.

        Commands: serve, run, list, validate, git init, new-admin-invite.

        `new-admin-invite` prints a fresh Server Admin invite code. Self-hosted with
        no email means a lost device is otherwise a permanent lockout; anyone with a
        shell on this box could do worse, so this is not a new exposure.

        Jobs are *.jobs.yaml files beside your notebooks. Example:

          notebook: ./nightly.nb.md
          defaults:
            parameters: {env: prod}
          jobs:
            - name: nightly-us
              cron: "0 2 * * *"
              parameters: {region: us}
        """;

    public static async Task<int> Main(string[] args) {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
            Console.WriteLine(_usage);
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];
        string jobName = null;
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++) {
            var arg = args[i];
            if (arg is "-h" or "--help") {
                Console.WriteLine(_usage);
                return 0;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal)) {
                if (i + 1 >= args.Length) {
                    Console.Error.WriteLine($"{arg} requires a value.");
                    return 2;
                }
                flags[arg[2..]] = args[++i];
            } else if (jobName == null) {
                jobName = arg;
            } else {
                Console.Error.WriteLine($"Unexpected extra argument: {arg}");
                return 2;
            }
        }

        JobsOptions options;
        try {
            options = JobsOptions.Resolve(flags);
        } catch (Exception e) {
            Console.Error.WriteLine($"Bad configuration: {e.Message}");
            return 2;
        }

        // `git init` etc: the sub-verb arrives as the bare argument.
        if (command == "git") {
            return GitCommand(options, jobName);
        }

        using var registryLogging = LoggerFactory.Create(b => b.AddConsole());
        var projects = new ProjectRegistry(options, registryLogging);
        // Before any layout is consulted: a 0.9 workspace has dev/ where test/
        // belongs, so it only looks intact once this has run.
        foreach (var migrated in projects.PrepareWorkspaces()) {
            Console.Error.WriteLine(
                $"{migrated.Root}: renamed the dev branch and worktree to test. " +
                "A configured remote keeps its old dev branch — delete it there when you are ready.");
        }
        switch (command) {
            case "new-admin-invite":
                return await NewAdminInviteAsync(options);
            case "serve":
                return await ServeAsync(projects, options);
            case "list":
                return List(projects);
            case "validate":
                return Validate(projects);
            case "run":
                if (jobName == null) {
                    Console.Error.WriteLine("run needs a job name. See `clrkernel-jobs --help`.");
                    return 2;
                }
                return await RunAsync(projects, options, jobName);
            default:
                Console.Error.WriteLine($"Unknown command: {command}. See `clrkernel-jobs --help`.");
                return 2;
        }
    }

    /// <summary>
    /// The way back in. Prints one single-use Server Admin invite and exits; it
    /// touches nothing else, so it is safe to run against a live server.
    /// </summary>
    private static async Task<int> NewAdminInviteAsync(JobsOptions options) {
        IAuthStore store;
        try {
            // Create() migrates on the way out, which matters because this may be
            // the only command anyone has ever run against this data directory.
            Directory.CreateDirectory(options.DataDir);
            RunStoreFactory.Create(options);
            store = RunStoreFactory.CreateAuthStore(options);
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var invite = await store.CreateInviteAsync(
            AuthService.NewInviteCode(), UserRole.ServerAdmin, "created from the command line",
            null, DateTime.UtcNow, TimeSpan.FromDays(options.InviteLifetimeDays));
        // The configured origin, not the bind url: on a real server --urls is
        // something like http://0.0.0.0:5000, and this printed link is the entire
        // delivery mechanism for the way back in.
        var origin = options.Origins.FirstOrDefault() ?? "http://localhost:5000";
        Console.WriteLine(invite.Code);
        Console.WriteLine($"{origin}/invite/{invite.Code}");
        Console.Error.WriteLine(
            $"Single use, expires {invite.ExpiresAt:u}. Opening it creates a new Server Admin.");
        return 0;
    }

    private static GitService GitFor(JobsOptions options) =>
        new(options.NotebooksRoot,
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<GitService>(),
            options.GitAuthorName, options.GitAuthorEmail);

    private static int GitCommand(JobsOptions options, string subVerb) {
        if (subVerb != "init") {
            Console.Error.WriteLine("Usage: clrkernel-jobs git init [--notebooks <dir>]");
            return 2;
        }
        try {
            var message = GitFor(options).Init();
            Console.WriteLine($"{options.NotebooksRoot}: {message}");
            if (!options.GitEnabled) {
                // Initializing implies wanting the workflow: persist the flag so
                // serve/list/run pick up the test/prod layout without extra flags.
                var registry = new SettingsRegistry(options);
                registry.Add(new SettingsSection {
                    Key = "git",
                    Title = "Git",
                    Fields = { new SettingField { Name = "gitEnabled", Type = "bool", WebWritable = true } },
                });
                registry.Write("git", new Dictionary<string, System.Text.Json.JsonElement> {
                    ["gitEnabled"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                });
                Console.WriteLine("gitEnabled=true written to settings.json.");
            }
            return 0;
        } catch (GitException e) {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>
    /// The serve precondition: the store must be chosen, not defaulted. A server
    /// that silently lands on sqlite looks fine until the run history turns up in
    /// the wrong place. Returns the error message, or null when configured.
    /// </summary>
    internal static string MissingStoreError(JobsOptions options) =>
        options.IsExplicit("store") ? null :
            """
            serve needs an explicit run-history store.

              --store sqlite       zero config; the database lives in the data dir
              --store files        no database; run.json beside each run's artifacts
              --store postgres     needs --connection-string (or CLRKERNEL_JOBS_CONNECTION)
              --store sqlserver    needs --connection-string (or CLRKERNEL_JOBS_CONNECTION)

            Set it with --store, CLRKERNEL_JOBS_STORE, or "store" in settings.json.
            (One-shot commands like `run` still default to sqlite.)
            """;

    private static async Task<int> ServeAsync(ProjectRegistry projects, JobsOptions options) {
        if (MissingStoreError(options) is { } missingStore) {
            Console.Error.WriteLine(missingStore);
            return 2;
        }

        var result = projects.LoadAll();
        Console.WriteLine($"clrkernel-jobs scheduler — {result.Jobs.Count} job(s) in " +
            $"{projects.Projects.Count} project(s)");
        PrintErrors(result.Errors);

        if ((options.Store ?? "sqlite").Equals("files", StringComparison.OrdinalIgnoreCase)) {
            Console.Error.WriteLine(
                "serve needs a database: user accounts and sessions have nowhere to live under " +
                "--store files. Use --store sqlite (the default, zero config).");
            return 2;
        }

        Directory.CreateDirectory(options.DataDir);
        IRunStore store;
        try {
            // Prove the store works before binding the port: a half-configured
            // server should exit with guidance, not serve 500s.
            store = RunStoreFactory.Create(options, waitForDatabase: true, log: Console.Error.WriteLine);
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var app = BuildApp(options, projects, store);
        var urls = options.Urls ?? "http://localhost:5000";
        if (!JobsApi.IsLocalOnly(urls) && options.RelyingPartyId == "localhost") {
            Console.Error.WriteLine(
                "  ! Listening beyond localhost with the relying party id still 'localhost'. " +
                "Passkeys are bound to a domain and cannot be moved, so set --rp-id (and serve " +
                "over HTTPS) before anyone registers one.");
        }
        Console.WriteLine($"API on {urls} (sign-in required; passkeys bound to {options.RelyingPartyId})");
        await app.RunAsync(urls);
        return 0;
    }

    /// <summary>The scheduler + API host. Shared with the integration tests.</summary>
    internal static WebApplication BuildApp(
        JobsOptions options, ProjectRegistry projects, IRunStore store, IAuthStore authStore = null) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // Statuses and triggers go over the wire as their names, matching how they
        // are stored and keeping the SPA free of magic numbers.
        // Enum names as declared. Deliberately NOT camelCase: RunStatus etc. are
        // part of the public API shape ("Succeeded"), and the SPA and anything else
        // reading /api/runs compares against those names. Where a payload's casing
        // differs from the kernel's RPC — connection setting kinds — the client
        // compares case-insensitively rather than this contract being bent.
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(projects);
        builder.Services.AddSingleton(store);
        // Accounts share the run database. Tests hand one in so the host does not
        // have to re-derive a connection from options it never had.
        builder.Services.AddSingleton(authStore ?? RunStoreFactory.CreateAuthStore(options));
        builder.Services.AddSingleton(provider => new AuthService(
            provider.GetRequiredService<IAuthStore>(), options,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<AuthService>()));

        var settings = SettingsRegistry.CreateDefault(options);
        settings.Add(new SettingsSection {
            Key = "git",
            // One word: this title is also the label of the Settings tab that
            // routes to /settings/git.
            Title = "Git",
            Description = options.GitEnabled
                ? "Test→prod promotion is on: edits commit to the test branch, approvals merge to main."
                : "Off. Run `clrkernel-jobs git init` on the notebooks root to enable test→prod promotion.",
            Fields = {
                new SettingField {
                    Name = "gitEnabled", Label = "Enabled", Type = "bool",
                    Value = options.GitEnabled, Source = options.SourceOf("gitEnabled"),
                    WebWritable = false, RestartRequired = true,
                },
                new SettingField {
                    Name = "gitAuthorName", Label = "Commit author", Type = "string",
                    Value = options.GitAuthorName ?? "clrkernel-jobs",
                    Source = options.SourceOf("gitAuthorName"),
                    WebWritable = true, RestartRequired = true,
                },
                new SettingField {
                    Name = "gitAuthorEmail", Label = "Commit email", Type = "string",
                    Value = options.GitAuthorEmail ?? "jobs@clrkernel.local",
                    Source = options.SourceOf("gitAuthorEmail"),
                    WebWritable = true, RestartRequired = true,
                },
                new SettingField {
                    Name = "gitPushRemote", Label = "Push remote", Type = "string",
                    Value = options.GitPushRemote ?? "",
                    Source = options.SourceOf("gitPushRemote"),
                    WebWritable = true, RestartRequired = true,
                    Help = "Remote url/name to push test and main to after commits. " +
                        "Credentials come from the environment (ssh agent, token in the url) — never stored.",
                },
            },
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(provider => new JobExecutor(
            store, options, provider.GetRequiredService<ILoggerFactory>().CreateLogger<JobExecutor>(),
            projects));
        builder.Services.AddSingleton(provider => new Notifier(
            options, provider.GetRequiredService<ILoggerFactory>().CreateLogger<Notifier>()));
        // What the kernel can run, for parsing notebooks and filling the editor's
        // language picker. Probed lazily on first use, never at startup.
        builder.Services.AddSingleton(provider => new KernelLanguages(
            options, provider.GetRequiredService<ILoggerFactory>().CreateLogger<KernelLanguages>()));
        // Warm kernels for the web editor: one per notebook, evicted when idle.
        builder.Services.AddSingleton<NotebookSessionManager>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<NotebookSessionManager>());
        builder.Services.AddSingleton<SchedulerService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SchedulerService>());

        var app = builder.Build();
        // Before the routes: every handler downstream can then ask who the caller
        // is without each one repeating the cookie lookup.
        app.UseMiddleware<AuthenticationMiddleware>();
        app.MapAuthApi();
        app.MapJobsApi();

        // The SPA, when it has been built (webapp/ -> wwwroot/). Absent in a
        // source build that skipped npm, and the API still works without it.
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot)) {
            app.UseDefaultFiles(new DefaultFilesOptions {
                FileProvider = new PhysicalFileProvider(wwwroot),
            });
            app.UseStaticFiles(new StaticFileOptions {
                FileProvider = new PhysicalFileProvider(wwwroot),
            });
            // Client-side routes (/jobs/x, /runs/id) are served the shell; /api is
            // already handled above and must not fall through to it.
            app.MapFallbackToFile("index.html", new StaticFileOptions {
                FileProvider = new PhysicalFileProvider(wwwroot),
            });
        }
        return app;
    }

    private static int List(ProjectRegistry projects) {
        var result = projects.LoadAll();
        if (result.Jobs.Count == 0 && result.Errors.Count == 0) {
            Console.WriteLine($"No *.jobs.yaml files under {projects.Default.Root}.");
            return 0;
        }
        var many = projects.Projects.Count > 1;
        foreach (var job in result.Jobs
                     .OrderBy(j => j.Project).ThenBy(j => j.Environment)
                     .ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)) {
            var schedule = job.Cron != null ? $"cron '{job.Cron}'" : "manual";
            var deps = job.DependsOn.Count > 0 ? $", needs {string.Join(", ", job.DependsOn)}" : string.Empty;
            var disabled = job.Enabled ? string.Empty : " [disabled]";
            var env = job.Environment == "default" ? string.Empty : $"[{job.Environment}] ";
            var project = many ? $"{job.Project}/" : string.Empty;
            Console.WriteLine($"  {project}{env}{job.Name}{disabled} — {job.NotebookRelative} ({schedule}{deps})");
        }
        PrintErrors(result.Errors);
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static int Validate(ProjectRegistry projects) {
        var result = projects.LoadAll();
        Console.WriteLine($"{result.Jobs.Count} job(s) in {projects.Projects.Count} project(s)");
        PrintErrors(result.Errors);
        Console.WriteLine(result.Errors.Count == 0 ? "OK" : $"{result.Errors.Count} problem(s).");
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static void PrintErrors(IReadOnlyList<string> errors) {
        foreach (var error in errors) {
            Console.Error.WriteLine($"  ! {error}");
        }
    }

    private static async Task<int> RunAsync(
        ProjectRegistry projects, JobsOptions options, string jobName) {
        // ponytail: `run` targets one project — the default — because it is the
        // one-shot command and nobody has asked to name another. Add --project when
        // somebody does.
        var project = projects.Default;
        var catalog = projects.CatalogFor(project);
        var environment = catalog.GitLayout
            ? (options.RunEnvironment ?? GitService.TestBranch)
            : "default";
        var result = catalog.Load();
        var job = result.Find(project.Slug, environment, jobName);
        if (job == null) {
            Console.Error.WriteLine(result.Jobs.Count == 0
                ? $"No jobs found under {catalog.NotebooksRoot}."
                : $"No job named '{jobName}' in {environment}. Known: " +
                  string.Join(", ", result.In(project.Slug, environment).Select(j => j.Name)) + ".");
            PrintErrors(result.Errors);
            return 2;
        }
        // A broken tree still runs an unaffected job, but the problems are shown.
        PrintErrors(result.Errors);

        if ((options.Store ?? "sqlite").Equals("files", StringComparison.OrdinalIgnoreCase)) {
            Console.Error.WriteLine(
                "serve needs a database: user accounts and sessions have nowhere to live under " +
                "--store files. Use --store sqlite (the default, zero config).");
            return 2;
        }

        Directory.CreateDirectory(options.DataDir);
        IRunStore store;
        try {
            store = RunStoreFactory.Create(options);
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        using var loggerFactory = LoggerFactory.Create(builder => {
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        var executor = new JobExecutor(
            store, options, loggerFactory.CreateLogger<JobExecutor>(), projects);
        executor.CellProgress += (_, cell, total) => {
            var step = $"[{cell.CellIndex + 1}/{total}]";
            switch (cell.Status) {
                case CellStatus.Running:
                    Console.WriteLine($"{step} {cell.SourcePreview}");
                    break;
                case CellStatus.Succeeded:
                    Console.WriteLine($"{step} ok ({Elapsed(cell)})");
                    break;
                case CellStatus.Failed:
                    Console.WriteLine($"{step} FAILED: {cell.ErrorSummary}");
                    break;
                case CellStatus.Skipped:
                    Console.WriteLine($"{step} skipped");
                    break;
            }
        };

        using var cancelled = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            cancelled.Cancel();
        };

        Console.WriteLine($"Running {job.Name} ({job.NotebookRelative})");
        var run = await executor.ExecuteAsync(job, RunTrigger.Manual, cancellationToken: cancelled.Token);

        Console.WriteLine($"{run.Status}: {run.JobName} in {Elapsed(run)}");
        if (run.ErrorSummary != null) {
            Console.WriteLine($"  {run.ErrorSummary}");
        }
        Console.WriteLine($"  artifact: {Path.Combine(options.DataDir, run.ArtifactPath)}");
        Console.WriteLine($"  log:      {Path.Combine(options.DataDir, run.LogPath)}");

        // A one-shot CLI run notifies too — this is how someone driving jobs from
        // their own cron/systemd instead of `serve` still gets alerts.
        await new Notifier(options, loggerFactory.CreateLogger<Notifier>()).NotifyAsync(job, run);
        return run.Status == RunStatus.Succeeded ? 0 : 1;
    }

    private static string Elapsed(RunCell cell) =>
        cell.StartedAt is { } start && cell.FinishedAt is { } end
            ? $"{(end - start).TotalSeconds:0.0}s" : "?";

    private static string Elapsed(Run run) =>
        run.StartedAt is { } start && run.FinishedAt is { } end
            ? $"{(end - start).TotalSeconds:0.0}s" : "?";
}
