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
          list            List the jobs found under the notebooks root.
          validate        Parse and validate every *.jobs.yaml; exit 1 on problems.

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
          --api-key <key>            serve: require this key in the X-Api-Key
                                     header on /api/* (or CLRKERNEL_JOBS_APIKEY).
          --max-parallelism <n>      serve: concurrent runs (default 4).
          -h, --help                 Show this help.

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

        var catalog = new JobCatalog(options.NotebooksRoot);
        switch (command) {
            case "serve":
                return await ServeAsync(catalog, options);
            case "list":
                return List(catalog);
            case "validate":
                return Validate(catalog);
            case "run":
                if (jobName == null) {
                    Console.Error.WriteLine("run needs a job name. See `clrkernel-jobs --help`.");
                    return 2;
                }
                return await RunAsync(catalog, options, jobName);
            default:
                Console.Error.WriteLine($"Unknown command: {command}. See `clrkernel-jobs --help`.");
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

    private static async Task<int> ServeAsync(JobCatalog catalog, JobsOptions options) {
        if (MissingStoreError(options) is { } missingStore) {
            Console.Error.WriteLine(missingStore);
            return 2;
        }

        var result = catalog.Load();
        Console.WriteLine($"clrkernel-jobs scheduler — {result.Jobs.Count} job(s) under {catalog.NotebooksRoot}");
        PrintErrors(result.Errors);

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

        var app = BuildApp(options, catalog, store);
        var urls = options.Urls ?? "http://localhost:5000";
        if (options.ApiKey == null && !urls.Contains("localhost") && !urls.Contains("127.0.0.1")) {
            Console.Error.WriteLine(
                "  ! Listening beyond localhost with no API key. Set --api-key (or CLRKERNEL_JOBS_APIKEY).");
        }
        Console.WriteLine($"API on {urls}{(options.ApiKey != null ? " (X-Api-Key required)" : string.Empty)}");
        await app.RunAsync(urls);
        return 0;
    }

    /// <summary>The scheduler + API host. Shared with the integration tests.</summary>
    internal static WebApplication BuildApp(JobsOptions options, JobCatalog catalog, IRunStore store) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // Statuses and triggers go over the wire as their names, matching how they
        // are stored and keeping the SPA free of magic numbers.
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(SettingsRegistry.CreateDefault(options));
        builder.Services.AddSingleton(provider => new JobExecutor(
            store, options, provider.GetRequiredService<ILoggerFactory>().CreateLogger<JobExecutor>()));
        builder.Services.AddSingleton(provider => new Notifier(
            options, provider.GetRequiredService<ILoggerFactory>().CreateLogger<Notifier>()));
        builder.Services.AddSingleton<SchedulerService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SchedulerService>());

        var app = builder.Build();
        app.UseMiddleware<ApiKeyMiddleware>();
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

    private static int List(JobCatalog catalog) {
        var result = catalog.Load();
        if (result.Jobs.Count == 0 && result.Errors.Count == 0) {
            Console.WriteLine($"No *.jobs.yaml files under {catalog.NotebooksRoot}.");
            return 0;
        }
        foreach (var job in result.Jobs.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase)) {
            var schedule = job.Cron != null ? $"cron '{job.Cron}'" : "manual";
            var deps = job.DependsOn.Count > 0 ? $", needs {string.Join(", ", job.DependsOn)}" : string.Empty;
            var disabled = job.Enabled ? string.Empty : " [disabled]";
            Console.WriteLine($"  {job.Name}{disabled} — {job.NotebookRelative} ({schedule}{deps})");
        }
        PrintErrors(result.Errors);
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static int Validate(JobCatalog catalog) {
        var result = catalog.Load();
        Console.WriteLine($"{result.Jobs.Count} job(s) in {catalog.NotebooksRoot}");
        PrintErrors(result.Errors);
        Console.WriteLine(result.Errors.Count == 0 ? "OK" : $"{result.Errors.Count} problem(s).");
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static void PrintErrors(IReadOnlyList<string> errors) {
        foreach (var error in errors) {
            Console.Error.WriteLine($"  ! {error}");
        }
    }

    private static async Task<int> RunAsync(JobCatalog catalog, JobsOptions options, string jobName) {
        var result = catalog.Load();
        var job = result.Find(jobName);
        if (job == null) {
            Console.Error.WriteLine(result.Jobs.Count == 0
                ? $"No jobs found under {catalog.NotebooksRoot}."
                : $"No job named '{jobName}'. Known jobs: {string.Join(", ", result.Jobs.Select(j => j.Name))}.");
            PrintErrors(result.Errors);
            return 2;
        }
        // A broken tree still runs an unaffected job, but the problems are shown.
        PrintErrors(result.Errors);

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
        var executor = new JobExecutor(store, options, loggerFactory.CreateLogger<JobExecutor>());
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
