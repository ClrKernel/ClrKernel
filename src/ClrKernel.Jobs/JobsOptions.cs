using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClrKernel.Jobs;

/// <summary>
/// Resolved settings for the jobs tool. Layered: CLI flags override environment
/// variables (<c>CLRKERNEL_JOBS_*</c>), which override <c>settings.json</c> in the
/// data directory, which overrides defaults (notebooks root = current directory,
/// data dir = <c>~/.clrkernel/jobs</c>, store = sqlite).
/// </summary>
public sealed class JobsOptions {
    public string NotebooksRoot { get; set; }
    public string DataDir { get; set; }
    /// <summary>sqlite (default) | sqlserver | postgres | files.</summary>
    public string Store { get; set; } = "sqlite";
    public string ConnectionString { get; set; }
    /// <summary>Explicit path to the clrkernel executable; null = probe PATH and ~/.dotnet/tools.</summary>
    public string ClrKernelPath { get; set; }
    public int MaxParallelism { get; set; } = 4;
    /// <summary>When set, /api/* requires this key in the X-Api-Key header.</summary>
    public string ApiKey { get; set; }
    /// <summary>Listen urls for serve; null = http://localhost:5000.</summary>
    public string Urls { get; set; }

    public string ArtifactsDir => Path.Combine(DataDir, "artifacts");
    public string DefaultSqlitePath => Path.Combine(DataDir, "jobs.db");

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clrkernel", "jobs");

    /// <summary>Builds options from parsed CLI flags, applying the env/settings/default layers.</summary>
    public static JobsOptions Resolve(IReadOnlyDictionary<string, string> cliFlags) {
        string Cli(string name) => cliFlags.TryGetValue(name, out var v) ? v : null;
        string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var dataDir = Cli("data-dir") ?? Env("CLRKERNEL_JOBS_DATA") ?? DefaultDataDir;

        JsonElement settings = default;
        var settingsPath = Path.Combine(dataDir, "settings.json");
        if (File.Exists(settingsPath)) {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            settings = doc.RootElement.Clone();
        }
        string Setting(string name) =>
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                : null;

        var options = new JobsOptions {
            DataDir = Path.GetFullPath(dataDir),
            NotebooksRoot = Path.GetFullPath(
                Cli("notebooks") ?? Env("CLRKERNEL_JOBS_NOTEBOOKS") ?? Setting("notebooksRoot") ?? Directory.GetCurrentDirectory()),
            Store = Cli("store") ?? Env("CLRKERNEL_JOBS_STORE") ?? Setting("store") ?? "sqlite",
            ConnectionString = Cli("connection-string") ?? Env("CLRKERNEL_JOBS_CONNECTION") ?? Setting("connectionString"),
            ClrKernelPath = Cli("clrkernel") ?? Env("CLRKERNEL_JOBS_CLRKERNEL") ?? Setting("clrkernelPath"),
            ApiKey = Cli("api-key") ?? Env("CLRKERNEL_JOBS_APIKEY") ?? Setting("apiKey"),
            Urls = Cli("urls") ?? Env("CLRKERNEL_JOBS_URLS") ?? Setting("urls"),
        };
        var parallelism = Cli("max-parallelism") ?? Env("CLRKERNEL_JOBS_MAX_PARALLELISM") ?? Setting("maxParallelism");
        if (parallelism != null && int.TryParse(parallelism, out var p) && p > 0) {
            options.MaxParallelism = p;
        }
        return options;
    }
}
