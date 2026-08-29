using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Phase-1 configuration hardening: every resolved option knows which layer
/// supplied it, serve refuses to start on a defaulted store, and an unreachable
/// database fails fast with guidance (after bounded retries on the serve path).
/// </summary>
[TestClass]
public class StoreConfigTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs a block with an environment variable set, restoring it after.</summary>
    private static void WithEnv(string name, string value, Action body) {
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try {
            body();
        } finally {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    /// <summary>
    /// A wildcard bind is not an origin. No browser presents
    /// <c>http://0.0.0.0:5000</c> as an Origin header and none can open it, so the
    /// default-from-urls path must not leave one in the allowed list — or in the
    /// invite link, which is built from the same array and is the whole way back in.
    /// </summary>
    [TestMethod]
    public void A_wildcard_bind_address_becomes_a_reachable_origin() {
        var wildcard = JobsOptions.Resolve(new Dictionary<string, string> {
            ["data-dir"] = _dir,
            ["urls"] = "http://0.0.0.0:5000",
        });
        CollectionAssert.AreEqual(new[] { "http://localhost:5000" }, wildcard.Origins);

        Assert.AreEqual("http://localhost:5000", JobsOptions.Reachable("http://[::]:5000"));
        Assert.AreEqual("http://localhost:5000", JobsOptions.Reachable("http://*:5000"));
        Assert.AreEqual("http://localhost:5000", JobsOptions.Reachable("http://+:5000"));

        // A real host is left exactly as written, wildcard-looking port and all.
        Assert.AreEqual("https://studio.example.com", JobsOptions.Reachable("https://studio.example.com"));
        var explicitly = JobsOptions.Resolve(new Dictionary<string, string> {
            ["data-dir"] = _dir,
            ["urls"] = "http://0.0.0.0:5000",
            ["origins"] = "https://studio.example.com",
        });
        CollectionAssert.AreEqual(new[] { "https://studio.example.com" }, explicitly.Origins);
    }

    [TestMethod]
    public void Every_layer_reports_its_provenance() {
        // CLI wins and says so.
        var fromCli = JobsOptions.Resolve(new Dictionary<string, string> {
            ["store"] = "postgres",
            ["data-dir"] = _dir,
        });
        Assert.AreEqual("postgres", fromCli.Store);
        Assert.AreEqual("--store", fromCli.SourceOf("store"));
        Assert.IsTrue(fromCli.IsExplicit("store"));

        // Environment next.
        WithEnv("CLRKERNEL_STUDIO_STORE", "files", () => {
            var fromEnv = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
            Assert.AreEqual("files", fromEnv.Store);
            Assert.AreEqual("CLRKERNEL_STUDIO_STORE", fromEnv.SourceOf("store"));
        });

        // settings.json in the data dir.
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"store\":\"sqlite\"}");
        var fromSettings = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
        Assert.AreEqual("sqlite", fromSettings.Store);
        Assert.AreEqual("settings.json", fromSettings.SourceOf("store"));

        // Nothing anywhere: the default, and it admits to being one.
        File.Delete(Path.Combine(_dir, "settings.json"));
        var defaulted = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
        Assert.AreEqual("sqlite", defaulted.Store);
        Assert.AreEqual("default", defaulted.SourceOf("store"));
        Assert.IsFalse(defaulted.IsExplicit("store"));
    }

    [TestMethod]
    public void Cli_beats_env_beats_settings() {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"store\":\"files\"}");
        WithEnv("CLRKERNEL_STUDIO_STORE", "postgres", () => {
            var both = JobsOptions.Resolve(new Dictionary<string, string> {
                ["store"] = "sqlite",
                ["data-dir"] = _dir,
            });
            Assert.AreEqual("sqlite", both.Store, "CLI wins");

            var envOnly = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
            Assert.AreEqual("postgres", envOnly.Store, "env beats settings.json");
        });
    }

    [TestMethod]
    public void Serve_refuses_a_defaulted_store_but_accepts_an_explicit_one() {
        var defaulted = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
        var error = Program.MissingStoreError(defaulted);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "--store");
        StringAssert.Contains(error, "CLRKERNEL_STUDIO_STORE");

        var explicitSqlite = JobsOptions.Resolve(new Dictionary<string, string> {
            ["store"] = "sqlite",
            ["data-dir"] = _dir,
        });
        Assert.IsNull(Program.MissingStoreError(explicitSqlite));
    }

    [TestMethod]
    public void An_unreachable_database_fails_with_guidance_naming_the_source() {
        var options = JobsOptions.Resolve(new Dictionary<string, string> {
            ["store"] = "postgres",
            // Port 9 is discard; nothing answers, and the failure is immediate.
            ["connection-string"] = "Host=127.0.0.1;Port=9;Database=x;Username=u;Password=p;Timeout=1",
            ["data-dir"] = _dir,
        });

        var e = Assert.ThrowsExactly<InvalidOperationException>(() => RunStoreFactory.Create(options));
        StringAssert.Contains(e.Message, "postgres");
        StringAssert.Contains(e.Message, "--store", "the message names where the store came from");
        StringAssert.Contains(e.Message, "--connection-string");
    }

    [TestMethod]
    public void The_serve_path_retries_before_giving_up() {
        var options = JobsOptions.Resolve(new Dictionary<string, string> {
            ["store"] = "postgres",
            ["connection-string"] = "Host=127.0.0.1;Port=9;Database=x;Username=u;Password=p;Timeout=1",
            ["data-dir"] = _dir,
        });

        var originalDelays = RunStoreFactory.RetryDelays;
        var retriesLogged = 0;
        try {
            RunStoreFactory.RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                RunStoreFactory.Create(options, waitForDatabase: true, log: _ => retriesLogged++));
        } finally {
            RunStoreFactory.RetryDelays = originalDelays;
        }
        Assert.AreEqual(2, retriesLogged, "one log line per retry, then the final failure");
    }

    [TestMethod]
    public void One_shot_commands_do_not_retry() {
        var options = JobsOptions.Resolve(new Dictionary<string, string> {
            ["store"] = "postgres",
            ["connection-string"] = "Host=127.0.0.1;Port=9;Database=x;Username=u;Password=p;Timeout=1",
            ["data-dir"] = _dir,
        });

        var logged = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            RunStoreFactory.Create(options, waitForDatabase: false, log: _ => logged++));
        Assert.AreEqual(0, logged, "no retries without waitForDatabase");
    }

    /// <summary>
    /// <c>CLRKERNEL_JOBS_*</c> is the name these variables had while the tool was
    /// being built, before it was renamed to Studio — which happened before the
    /// first release, so nothing ever shipped under it. What this pins is that a
    /// source build or dev script set up back then still gets its value, and that
    /// the settings UI names the spelling that actually supplied it: a variable
    /// silently ignored does not fail, it falls back to a default.
    /// </summary>
    [TestMethod]
    public void The_pre_rename_environment_variables_still_work_and_say_which_one_was_read() {
        WithEnv("CLRKERNEL_JOBS_STORE", "postgres", () => {
            var resolved = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
            Assert.AreEqual("postgres", resolved.Store);
            Assert.AreEqual("CLRKERNEL_JOBS_STORE", resolved.SourceOf("store"),
                "the message has to name the variable that is actually set, not the new spelling");
            Assert.IsTrue(resolved.IsExplicit("store"));
        });
    }

    [TestMethod]
    public void The_new_spelling_wins_when_both_are_set() {
        WithEnv("CLRKERNEL_JOBS_STORE", "postgres", () => {
            WithEnv("CLRKERNEL_STUDIO_STORE", "sqlserver", () => {
                var resolved = JobsOptions.Resolve(new Dictionary<string, string> { ["data-dir"] = _dir });
                Assert.AreEqual("sqlserver", resolved.Store);
                Assert.AreEqual("CLRKERNEL_STUDIO_STORE", resolved.SourceOf("store"));
            });
        });
    }

}
