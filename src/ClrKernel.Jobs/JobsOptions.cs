using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClrKernel.Jobs;

/// <summary>
/// Resolved settings for the jobs tool. Layered: CLI flags override environment
/// variables (<c>CLRKERNEL_JOBS_*</c>), which override <c>settings.json</c> in the
/// data directory, which overrides defaults (notebooks root = current directory,
/// data dir = <c>~/.clrkernel/jobs</c>).
/// <para>
/// Every resolved value remembers which layer supplied it (<see cref="SourceOf"/>):
/// error messages can say "store came from CLRKERNEL_JOBS_STORE", the settings UI
/// can lock CLI/env-pinned fields, and <c>serve</c> can require that the store was
/// chosen explicitly rather than defaulted.
/// </para>
/// </summary>
public sealed class JobsOptions {
    public string NotebooksRoot { get; set; }
    public string DataDir { get; set; }
    /// <summary>sqlite | sqlserver | postgres | files. Explicit for serve; sqlite by default elsewhere.</summary>
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

    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Where a value came from: <c>--flag</c>, the environment variable's name,
    /// <c>settings.json</c>, or <c>default</c>. Keys are the settings.json names
    /// (store, connectionString, notebooksRoot, dataDir, clrkernelPath, apiKey,
    /// urls, maxParallelism).
    /// </summary>
    public string SourceOf(string key) =>
        _sources.TryGetValue(key, out var source) ? source : "default";

    /// <summary>True when the value was supplied by any layer rather than defaulted.</summary>
    public bool IsExplicit(string key) => SourceOf(key) != "default";

    /// <summary>Builds options from parsed CLI flags, applying the env/settings/default layers.</summary>
    public static JobsOptions Resolve(IReadOnlyDictionary<string, string> cliFlags) {
        string Cli(string name) => cliFlags.TryGetValue(name, out var v) ? v : null;
        string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var options = new JobsOptions();
        string Pick(string key, string cliName, string envName, string settingValue, string fallback) {
            if (Cli(cliName) is { } fromCli) {
                options._sources[key] = "--" + cliName;
                return fromCli;
            }
            if (Env(envName) is { } fromEnv) {
                options._sources[key] = envName;
                return fromEnv;
            }
            if (settingValue != null) {
                options._sources[key] = "settings.json";
                return settingValue;
            }
            return fallback;
        }

        // The data dir is resolved first — settings.json lives inside it.
        var dataDir = Pick("dataDir", "data-dir", "CLRKERNEL_JOBS_DATA", null, DefaultDataDir);

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

        options.DataDir = Path.GetFullPath(dataDir);
        options.NotebooksRoot = Path.GetFullPath(Pick(
            "notebooksRoot", "notebooks", "CLRKERNEL_JOBS_NOTEBOOKS",
            Setting("notebooksRoot"), Directory.GetCurrentDirectory()));
        options.Store = Pick("store", "store", "CLRKERNEL_JOBS_STORE", Setting("store"), "sqlite");
        options.ConnectionString = Pick(
            "connectionString", "connection-string", "CLRKERNEL_JOBS_CONNECTION", Setting("connectionString"), null);
        options.ClrKernelPath = Pick(
            "clrkernelPath", "clrkernel", "CLRKERNEL_JOBS_CLRKERNEL", Setting("clrkernelPath"), null);
        options.ApiKey = Pick("apiKey", "api-key", "CLRKERNEL_JOBS_APIKEY", Setting("apiKey"), null);
        options.Urls = Pick("urls", "urls", "CLRKERNEL_JOBS_URLS", Setting("urls"), null);

        var parallelism = Pick(
            "maxParallelism", "max-parallelism", "CLRKERNEL_JOBS_MAX_PARALLELISM", Setting("maxParallelism"), null);
        if (parallelism != null && int.TryParse(parallelism, out var p) && p > 0) {
            options.MaxParallelism = p;
        }
        return options;
    }
}
