using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClrKernel.Studio;

/// <summary>
/// Resolved settings for the jobs tool. Layered: CLI flags override environment
/// variables (<c>CLRKERNEL_STUDIO_*</c>), which override <c>settings.json</c> in the
/// data directory, which overrides defaults (notebooks root = current directory,
/// data dir = <c>~/.clrkernel/jobs</c>).
/// <para>
/// Every resolved value remembers which layer supplied it (<see cref="SourceOf"/>):
/// error messages can say "store came from CLRKERNEL_STUDIO_STORE", the settings UI
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
    /// <summary>Listen urls for serve; null = http://localhost:5000.</summary>
    public string Urls { get; set; }
    /// <summary>Dev→prod workflow: the notebooks root is a git workspace with dev/prod worktrees.</summary>
    public bool GitEnabled { get; set; }

    /// <summary>
    /// Hold private connections to the same read-only rule as shared ones: no
    /// least-privilege login configured, no execution for anyone but a server admin.
    /// <para>
    /// Off by default, because a private connection is the person's own credential
    /// against a server they could reach with SSMS anyway — the app is not the
    /// security boundary there. On for installs that would rather it were.
    /// </para>
    /// </summary>
    public bool PrivateConnectionsReadOnly { get; set; }
    public string GitAuthorName { get; set; }
    public string GitAuthorEmail { get; set; }
    /// <summary>Remote name/url to push after commits and promotions; empty = local only.</summary>
    public string GitPushRemote { get; set; }
    /// <summary>The `run` verb's target environment (--env); null = dev in git mode.</summary>
    public string RunEnvironment { get; set; }

    /// <summary>
    /// The WebAuthn relying party id — the *domain* passkeys are bound to.
    /// <para>
    /// Configuration rather than a constant because a credential cannot be moved
    /// between relying parties: passkeys registered against <c>localhost</c> stop
    /// working the day the server answers to a real hostname, and everyone has to
    /// register again. Set this to the final hostname before anyone but you signs
    /// in. It must be the registrable domain of every origin below, or a suffix of
    /// it — <c>clrkernel.internal</c> covers <c>https://jobs.clrkernel.internal</c>.
    /// </para>
    /// </summary>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>
    /// Origins the browser is allowed to present. Defaults to whatever
    /// <see cref="Urls"/> says, so the common case needs no second setting; behind
    /// a TLS-terminating proxy the public origin differs from the bind url and has
    /// to be listed explicitly.
    /// </summary>
    public string[] Origins { get; set; } = Array.Empty<string>();

    /// <summary>How long a new invite stays usable.</summary>
    public int InviteLifetimeDays { get; set; } = 7;

    /// <summary>How long a signed-in browser stays signed in.</summary>
    public int SessionLifetimeDays { get; set; } = 14;

    /// <summary>
    /// How long a personal worktree may sit untouched before it is removed. Only
    /// ever applies to one that is clean and fully in test, so what goes is a copy
    /// of something that already exists. 0 turns the sweep off.
    /// </summary>
    public int WorktreeIdleDays { get; set; } = 30;

    /// <summary>
    /// How long finished runs are kept. <b>0 — the default — keeps them forever.</b>
    /// <para>
    /// Off by default on purpose: a first run after an upgrade that silently deleted
    /// somebody's history is not a default anyone can take back. Turn it on when the
    /// table and the artifact directory matter more than a year-old run does.
    /// </para>
    /// <para>
    /// A job's most recent run is never removed, whatever its age — it is what the
    /// promotion gate reads, and retention must not be able to make something
    /// unpromotable. Nor is anything still Pending or Running.
    /// </para>
    /// </summary>
    public int RunRetentionDays { get; set; }

    public string ArtifactsDir => Path.Combine(DataDir, "artifacts");
    public string DefaultSqlitePath => Path.Combine(DataDir, "jobs.db");

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clrkernel", "jobs");

    /// <summary>The pre-rename spelling of an environment variable name.</summary>
    internal static string LegacyEnvName(string name) =>
        name.StartsWith("CLRKERNEL_STUDIO_", StringComparison.Ordinal)
            ? "CLRKERNEL_JOBS_" + name["CLRKERNEL_STUDIO_".Length..]
            : name;

    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Where a value came from: <c>--flag</c>, the environment variable's name,
    /// <c>settings.json</c>, or <c>default</c>. Keys are the settings.json names
    /// (store, connectionString, notebooksRoot, dataDir, clrkernelPath, relyingPartyId,
    /// urls, maxParallelism).
    /// </summary>
    public string SourceOf(string key) =>
        _sources.TryGetValue(key, out var source) ? source : "default";

    /// <summary>True when the value was supplied by any layer rather than defaulted.</summary>
    public bool IsExplicit(string key) => SourceOf(key) != "default";

    /// <summary>
    /// Splits a `;` or `,` separated list and normalises each entry to a bare
    /// origin (scheme, host, port) — a trailing slash or path would never match
    /// what the browser reports.
    /// </summary>
    internal static string[] SplitList(string value) {
        var parts = new List<string>();
        foreach (var raw in (value ?? string.Empty).Split(new[] { ';', ',' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            parts.Add(Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : raw.TrimEnd('/'));
        }
        return parts.ToArray();
    }

    private static int PositiveInt(string value, int fallback) =>
        value != null && int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>Builds options from parsed CLI flags, applying the env/settings/default layers.</summary>
    public static JobsOptions Resolve(IReadOnlyDictionary<string, string> cliFlags) {
        string Cli(string name) => cliFlags.TryGetValue(name, out var v) ? v : null;
        // The `CLRKERNEL_JOBS_` spelling is still read, and this is not politeness:
        // the product was renamed after these were documented, and a deployment
        // that sets CLRKERNEL_JOBS_RPID would otherwise fall back to `localhost`
        // silently — which does not fail, it just stops every passkey working.
        // `SourceOf` reports whichever name actually supplied the value, so the
        // settings UI and the error messages name the one to change.
        string Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
                : Environment.GetEnvironmentVariable(LegacyEnvName(name)) is { Length: > 0 } old
                    ? old
                    : null;
        string EnvNameUsed(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } ? name : LegacyEnvName(name);

        var options = new JobsOptions();
        string Pick(string key, string cliName, string envName, string settingValue, string fallback) {
            if (Cli(cliName) is { } fromCli) {
                options._sources[key] = "--" + cliName;
                return fromCli;
            }
            if (Env(envName) is { } fromEnv) {
                options._sources[key] = EnvNameUsed(envName);
                return fromEnv;
            }
            if (settingValue != null) {
                options._sources[key] = "settings.json";
                return settingValue;
            }
            return fallback;
        }

        // The data dir is resolved first — settings.json lives inside it.
        var dataDir = Pick("dataDir", "data-dir", "CLRKERNEL_STUDIO_DATA", null, DefaultDataDir);

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
            "notebooksRoot", "notebooks", "CLRKERNEL_STUDIO_NOTEBOOKS",
            Setting("notebooksRoot"), Directory.GetCurrentDirectory()));
        options.Store = Pick("store", "store", "CLRKERNEL_STUDIO_STORE", Setting("store"), "sqlite");
        options.ConnectionString = Pick(
            "connectionString", "connection-string", "CLRKERNEL_STUDIO_CONNECTION", Setting("connectionString"), null);
        options.ClrKernelPath = Pick(
            "clrkernelPath", "clrkernel", "CLRKERNEL_STUDIO_CLRKERNEL", Setting("clrkernelPath"), null);
        options.Urls = Pick("urls", "urls", "CLRKERNEL_STUDIO_URLS", Setting("urls"), null);

        var parallelism = Pick(
            "maxParallelism", "max-parallelism", "CLRKERNEL_STUDIO_MAX_PARALLELISM", Setting("maxParallelism"), null);
        if (parallelism != null && int.TryParse(parallelism, out var p) && p > 0) {
            options.MaxParallelism = p;
        }

        var gitEnabled = Pick("gitEnabled", "git", "CLRKERNEL_STUDIO_GIT", Setting("gitEnabled"), null);
        options.GitEnabled = gitEnabled != null && bool.TryParse(gitEnabled, out var g) && g;
        var privateReadOnly = Pick(
            "privateConnectionsReadOnly", "private-connections-read-only",
            "CLRKERNEL_STUDIO_PRIVATE_READONLY", Setting("privateConnectionsReadOnly"), null);
        options.PrivateConnectionsReadOnly =
            privateReadOnly != null && bool.TryParse(privateReadOnly, out var pr) && pr;
        options.GitAuthorName = Pick(
            "gitAuthorName", "git-author-name", "CLRKERNEL_STUDIO_GIT_AUTHOR", Setting("gitAuthorName"), null);
        options.GitAuthorEmail = Pick(
            "gitAuthorEmail", "git-author-email", "CLRKERNEL_STUDIO_GIT_EMAIL", Setting("gitAuthorEmail"), null);
        options.GitPushRemote = Pick(
            "gitPushRemote", "git-push-remote", "CLRKERNEL_STUDIO_GIT_REMOTE", Setting("gitPushRemote"), null);
        options.RunEnvironment = Cli("env");

        options.RelyingPartyId = Pick(
            "relyingPartyId", "rp-id", "CLRKERNEL_STUDIO_RPID", Setting("relyingPartyId"), "localhost");
        var origins = Pick("origins", "origins", "CLRKERNEL_STUDIO_ORIGINS", Setting("origins"), null);
        options.Origins = origins != null
            ? SplitList(origins)
            // No explicit list: the origins are wherever this server listens. A
            // bind url is an origin already, minus any path.
            : SplitList(options.Urls ?? "http://localhost:5000");
        options.InviteLifetimeDays = PositiveInt(Pick(
            "inviteLifetimeDays", "invite-days", "CLRKERNEL_STUDIO_INVITE_DAYS",
            Setting("inviteLifetimeDays"), null), 7);
        options.SessionLifetimeDays = PositiveInt(Pick(
            "sessionLifetimeDays", "session-days", "CLRKERNEL_STUDIO_SESSION_DAYS",
            Setting("sessionLifetimeDays"), null), 14);
        var idleDays = Pick("worktreeIdleDays", "worktree-idle-days",
            "CLRKERNEL_STUDIO_WORKTREE_IDLE_DAYS", Setting("worktreeIdleDays"), null);
        if (idleDays != null && int.TryParse(idleDays, out var idle) && idle >= 0) {
            options.WorktreeIdleDays = idle;
        }
        var retention = Pick("runRetentionDays", "run-retention-days",
            "CLRKERNEL_STUDIO_RUN_RETENTION_DAYS", Setting("runRetentionDays"), null);
        if (retention != null && int.TryParse(retention, out var days) && days >= 0) {
            options.RunRetentionDays = days;
        }
        return options;
    }
}
