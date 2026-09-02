using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Webhook delivery against a real in-test listener, the channel file's shape and
/// validation, and the invariant that a broken channel never fails a run. SMTP is
/// exercised manually against a local mailpit (see docs) — MailKit's own transport
/// is not worth reimplementing a fake for.
/// </summary>
[TestClass]
public class NotifierTest {
    private string _root;
    private WebApplication _listener;
    private readonly List<(string Path, string Body, string Auth)> _received = new();

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _listener = builder.Build();
        _listener.MapPost("/hook", async (HttpContext context) => {
            using var reader = new StreamReader(context.Request.Body);
            _received.Add((context.Request.Path, await reader.ReadToEndAsync(),
                context.Request.Headers.Authorization.ToString()));
            return Results.Ok();
        });
        _listener.MapPost("/broken", () => Results.StatusCode(500));
        await _listener.StartAsync();
    }

    [TestCleanup]
    public async Task Cleanup() {
        await _listener.StopAsync();
        await _listener.DisposeAsync();
        TempDirectory.Delete(_root);
    }

    private string Url(string path) => _listener.Urls.First() + path;

    private JobsOptions Options => new() { DataDir = _root, NotebooksRoot = _root };

    private void WriteChannels(string yaml) =>
        File.WriteAllText(Path.Combine(_root, NotificationChannels.FileName), yaml);

    private static JobDefinition Job(string onFailure = null, string onSuccess = null) => new() {
        Name = "nightly",
        NotebookRelative = "nb.nb.md",
        Notify = new NotifyRules {
            OnFailure = onFailure == null ? new List<string>() : new List<string> { onFailure },
            OnSuccess = onSuccess == null ? new List<string>() : new List<string> { onSuccess },
        },
    };

    private static Run Finished(RunStatus status, string error = null) => new() {
        Id = Guid.NewGuid(),
        JobName = "nightly",
        NotebookPath = "nb.nb.md",
        Status = status,
        Trigger = RunTrigger.Schedule,
        CreatedAt = DateTime.UtcNow,
        StartedAt = DateTime.UtcNow,
        FinishedAt = DateTime.UtcNow,
        ErrorSummary = error,
        ArtifactPath = "artifacts/nightly/x/output.ipynb",
    };

    [TestMethod]
    public async Task A_failed_run_posts_to_the_webhook_its_job_names() {
        WriteChannels($"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n");
        var notifier = new Notifier(Options, NullLogger.Instance);

        await notifier.NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Failed, "cell 2: boom"));

        var payload = JsonDocument.Parse(_received.Single().Body).RootElement;
        Assert.AreEqual("nightly", payload.GetProperty("job").GetString());
        Assert.AreEqual("Failed", payload.GetProperty("status").GetString());
        Assert.IsFalse(payload.GetProperty("success").GetBoolean());
        StringAssert.Contains(payload.GetProperty("error").GetString(), "boom");
    }

    [TestMethod]
    public async Task Success_and_failure_rules_are_independent() {
        WriteChannels($"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n");
        var notifier = new Notifier(Options, NullLogger.Instance);

        // Only onFailure is set: a success must stay silent.
        await notifier.NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Succeeded));
        Assert.AreEqual(0, _received.Count);

        await notifier.NotifyAsync(Job(onSuccess: "ops"), Finished(RunStatus.Succeeded));
        Assert.AreEqual(1, _received.Count);
        Assert.IsTrue(JsonDocument.Parse(_received[0].Body).RootElement.GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task A_bearer_secret_reference_is_resolved_at_send_time() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n    bearerSecretRef: hook-token\n");
        var provider = new InMemorySecretProvider();
        provider.Set("hook-token", "s3cret");
        var secrets = SecretStore.ForProviders(provider);

        await new Notifier(Options, NullLogger.Instance, secrets)
            .NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Failed));

        Assert.AreEqual("Bearer s3cret", _received.Single().Auth,
            "the token is resolved from the secret store, never read from the channels file");
    }

    [TestMethod]
    public async Task A_missing_secret_is_reported_without_failing_the_run() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n    bearerSecretRef: absent-key\n");
        var notifier = new Notifier(Options, NullLogger.Instance, SecretStore.ForProviders());

        // NotifyAsync swallows channel errors by design: a broken notification must
        // not turn a successful run into a failed one.
        await notifier.NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Failed));
        Assert.AreEqual(0, _received.Count);

        // The same send surfaces the reason when called directly (the /test button).
        var channel = NotificationChannels.Load(_root).Find("ops");
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            notifier.SendAsync(channel, Notifier.Message.Test("ops")));
        StringAssert.Contains(e.Message, "absent-key");
    }

    [TestMethod]
    public async Task A_failing_webhook_never_breaks_the_run_but_surfaces_on_test() {
        WriteChannels($"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/broken")}\n");
        var notifier = new Notifier(Options, NullLogger.Instance);

        await notifier.NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Failed));

        var channel = NotificationChannels.Load(_root).Find("ops");
        var e = await Assert.ThrowsExactlyAsync<System.Net.Http.HttpRequestException>(() =>
            notifier.SendAsync(channel, Notifier.Message.Test("ops")));
        StringAssert.Contains(e.Message, "500");
    }

    [TestMethod]
    public async Task An_unknown_channel_name_is_ignored_rather_than_thrown() {
        WriteChannels("channels: []\n");
        await new Notifier(Options, NullLogger.Instance)
            .NotifyAsync(Job(onFailure: "does-not-exist"), Finished(RunStatus.Failed));
        Assert.AreEqual(0, _received.Count);
    }

    [TestMethod]
    public void Channel_validation_catches_the_common_mistakes() {
        WriteChannels(
            """
            channels:
              - name: no-url
                type: webhook
              - name: no-host
                type: email
                to: [ops@example.com]
              - name: no-recipients
                type: email
                host: smtp.example.com
              - name: bad-type
                type: carrier-pigeon
              - name: fine
                type: webhook
                url: https://example.com/hook
            """);
        var errors = NotificationChannels.Load(_root).Validate();

        Assert.IsTrue(errors.Any(e => e.Contains("no-url") && e.Contains("url")));
        Assert.IsTrue(errors.Any(e => e.Contains("no-host") && e.Contains("host")));
        Assert.IsTrue(errors.Any(e => e.Contains("no-recipients") && e.Contains("to")));
        Assert.IsTrue(errors.Any(e => e.Contains("bad-type") && e.Contains("carrier-pigeon")));
        Assert.IsFalse(errors.Any(e => e.Contains("'fine'")));
    }

    [TestMethod]
    public void Saving_channels_writes_only_real_configuration() {
        NotificationChannels.Save(_root, new NotificationChannels {
            Channels = {
                new ChannelConfig { Name = "ops", Type = "webhook", Url = "https://example.com/hook" },
            },
        });

        var yaml = File.ReadAllText(Path.Combine(_root, NotificationChannels.FileName));
        // The file is committed alongside notebooks, so it must not accumulate
        // computed properties or SMTP defaults that have no meaning for a webhook.
        Assert.IsFalse(yaml.Contains("isWebhook"), yaml);
        Assert.IsFalse(yaml.Contains("isEmail"), yaml);
        Assert.IsFalse(yaml.Contains("port"), yaml);
        Assert.IsFalse(yaml.Contains("startTls"), yaml);
        StringAssert.Contains(yaml, "url: https://example.com/hook");

        // And it round-trips.
        var reloaded = NotificationChannels.Load(_root).Find("ops");
        Assert.AreEqual("https://example.com/hook", reloaded.Url);
        Assert.IsTrue(reloaded.IsWebhook);
    }

    [TestMethod]
    public void Saving_an_invalid_channel_set_throws_before_writing() {
        var e = Assert.ThrowsExactly<InvalidDataException>(() =>
            NotificationChannels.Save(_root, new NotificationChannels {
                Channels = { new ChannelConfig { Name = "broken", Type = "webhook" } },
            }));
        StringAssert.Contains(e.Message, "url");
        Assert.IsFalse(File.Exists(Path.Combine(_root, NotificationChannels.FileName)));
    }

    [TestMethod]
    public void An_absent_channels_file_is_an_empty_set_not_an_error() {
        var channels = NotificationChannels.Load(_root);
        Assert.AreEqual(0, channels.Channels.Count);
        Assert.AreEqual(0, channels.Validate().Count);
        Assert.IsNull(channels.Find("anything"));
    }

    [TestMethod]
    public async Task Email_channels_refuse_to_send_without_a_password_reference() {
        WriteChannels(
            """
            channels:
              - name: mail
                type: email
                host: smtp.example.com
                user: sender@example.com
                to: [ops@example.com]
            """);
        var channel = NotificationChannels.Load(_root).Find("mail");

        // No passwordSecretRef and a user set: fail before opening a connection,
        // rather than prompting anyone to paste a password into the yaml.
        var e = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new Notifier(Options, NullLogger.Instance, SecretStore.ForProviders())
                .SendAsync(channel, Notifier.Message.Test("mail"), CancellationToken.None));
        StringAssert.Contains(e.Message, "secret reference");
    }
    // --- rules: when, as against where ---------------------------------------

    private (Notifier Notifier, EfRunStore Store) WithFeed() {
        var store = EfRunStore.Sqlite(Path.Combine(_root, "feed.db"));
        store.Migrate();
        return (new Notifier(Options, NullLogger.Instance, store: store), store);
    }

    private Task<IReadOnlyList<NotificationDelivery>> FeedAsync(EfRunStore store, bool failures = false) =>
        store.DeliveriesAsync(new NotificationQuery {
            Projects = new[] { "default" },
            FailuresOnly = failures,
        });

    /// <summary>
    /// A rule sends without the job asking. That is the whole point of the split:
    /// "tell us when anything in this project fails" belongs to whoever runs the
    /// project, not written into every job by hand.
    /// </summary>
    [TestMethod]
    public async Task A_rule_notifies_a_job_that_names_no_channel_itself() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n"
            + "rules:\n  - event: jobFailed\n    to: [ops]\n");
        var (notifier, store) = WithFeed();

        await notifier.NotifyAsync(Job(), Finished(RunStatus.Failed, "boom"));
        Assert.AreEqual(1, _received.Count);

        // And a success does not fire a failure rule.
        await notifier.NotifyAsync(Job(), Finished(RunStatus.Succeeded));
        Assert.AreEqual(1, _received.Count);

        var feed = await FeedAsync(store);
        Assert.AreEqual(1, feed.Count);
        Assert.AreEqual("JobFailed", feed[0].Event);
        Assert.AreEqual("ops", feed[0].Channel);
        Assert.IsNull(feed[0].Error, "it arrived");
    }

    [TestMethod]
    public async Task A_job_and_a_rule_naming_one_channel_send_one_message() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n"
            + "rules:\n  - event: jobFailed\n    to: [ops]\n");
        var (notifier, _) = WithFeed();

        await notifier.NotifyAsync(Job(onFailure: "ops"), Finished(RunStatus.Failed, "boom"));

        Assert.AreEqual(1, _received.Count, "one message, not one per source of the same name");
    }

    /// <summary>
    /// The all-clear. It needs the previous run because "recovered" is not a fact
    /// about this run — a green run after a green run is just Tuesday.
    /// </summary>
    [TestMethod]
    public async Task Recovered_fires_only_after_a_failure() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n"
            + "rules:\n  - event: jobRecovered\n    to: [ops]\n");
        var (notifier, store) = WithFeed();

        await notifier.NotifyAsync(Job(), Finished(RunStatus.Succeeded), Finished(RunStatus.Succeeded));
        Assert.AreEqual(0, _received.Count, "green after green is not a recovery");

        await notifier.NotifyAsync(Job(), Finished(RunStatus.Succeeded), Finished(RunStatus.Failed));
        Assert.AreEqual(1, _received.Count);
        Assert.AreEqual("JobRecovered", (await FeedAsync(store))[0].Event);

        // And the very first run of a job has nothing to have recovered from.
        _received.Clear();
        await notifier.NotifyAsync(Job(), Finished(RunStatus.Succeeded));
        Assert.AreEqual(0, _received.Count);
    }

    [TestMethod]
    public async Task Too_slow_fires_on_the_threshold_it_was_given() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n"
            + "rules:\n  - event: runTooSlow\n    afterSeconds: 60\n    to: [ops]\n");
        var (notifier, store) = WithFeed();

        var quick = Finished(RunStatus.Succeeded);
        quick.FinishedAt = quick.StartedAt!.Value.AddSeconds(5);
        await notifier.NotifyAsync(Job(), quick, Finished(RunStatus.Succeeded));
        Assert.AreEqual(0, _received.Count);

        var slow = Finished(RunStatus.Succeeded);
        slow.FinishedAt = slow.StartedAt!.Value.AddSeconds(120);
        await notifier.NotifyAsync(Job(), slow, Finished(RunStatus.Succeeded));
        Assert.AreEqual(1, _received.Count);
        Assert.AreEqual("RunTooSlow", (await FeedAsync(store))[0].Event);
    }

    /// <summary>
    /// The feed's reason for existing. A run went red, the rule fired, nobody heard —
    /// and every log said the notification was configured.
    /// </summary>
    [TestMethod]
    public async Task A_delivery_that_failed_is_in_the_feed_with_its_reason() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/broken")}\n"
            + "rules:\n  - event: jobFailed\n    to: [ops]\n");
        var (notifier, store) = WithFeed();

        // Still does not throw: a broken channel must never fail a run.
        await notifier.NotifyAsync(Job(), Finished(RunStatus.Failed, "boom"));

        var failures = await FeedAsync(store, failures: true);
        Assert.AreEqual(1, failures.Count);
        StringAssert.Contains(failures[0].Error, "500");
        Assert.AreEqual("ops", failures[0].Channel);
    }

    [TestMethod]
    public async Task A_promotion_tells_whoever_asked() {
        WriteChannels(
            $"channels:\n  - name: ops\n    type: webhook\n    url: {Url("/hook")}\n"
            + "rules:\n  - event: promotedToProd\n    to: [ops]\n");
        var (notifier, store) = WithFeed();

        await notifier.NotifyPromotionAsync(
            "default", new[] { "etl.nb.md", "etl.jobs.yaml" }, isDeletion: false, "Ada Lovelace");

        var payload = JsonDocument.Parse(_received.Single().Body).RootElement;
        Assert.AreEqual("PromotedToProd", payload.GetProperty("event").GetString());
        Assert.AreEqual("Ada Lovelace", payload.GetProperty("actor").GetString());
        Assert.AreEqual(2, payload.GetProperty("paths").GetArrayLength());
        Assert.AreEqual("PromotedToProd", (await FeedAsync(store))[0].Event);
    }

    [TestMethod]
    public void A_rule_pointing_at_a_channel_nobody_has_is_a_validation_error() {
        var config = new NotificationChannels {
            Channels = { new ChannelConfig { Name = "ops", Type = "webhook", Url = "http://x" } },
            Rules = {
                new NotificationRule { Event = NotificationEvent.JobFailed, To = { "typo" } },
                new NotificationRule { Event = NotificationEvent.RunTooSlow, To = { "ops" } },
                new NotificationRule { Event = NotificationEvent.JobFailed },
            },
        };
        var errors = config.Validate();

        // Caught here rather than at send time: each of these is a rule that looks
        // configured and never arrives.
        Assert.IsTrue(errors.Any(e => e.Contains("'typo'")), string.Join(" | ", errors));
        Assert.IsTrue(errors.Any(e => e.Contains("afterSeconds")), string.Join(" | ", errors));
        Assert.IsTrue(errors.Any(e => e.Contains("no channel to send to")), string.Join(" | ", errors));
    }

    /// <summary>
    /// Channels and rules share one file, so each half's save has to leave the other
    /// alone. The Channels page has no idea rules exist.
    /// </summary>
    [TestMethod]
    public void Saving_one_half_of_the_file_keeps_the_other() {
        WriteChannels(
            "channels:\n  - name: ops\n    type: webhook\n    url: http://x\n"
            + "rules:\n  - event: jobFailed\n    to: [ops]\n");

        var loaded = NotificationChannels.Load(_root);
        Assert.AreEqual(1, loaded.Rules.Count);

        // What the channels PUT does: read, replace one half, write.
        loaded.Channels = new List<ChannelConfig> {
            new() { Name = "ops", Type = "webhook", Url = "http://y" },
        };
        NotificationChannels.Save(_root, loaded);

        var again = NotificationChannels.Load(_root);
        Assert.AreEqual("http://y", again.Channels.Single().Url);
        Assert.AreEqual(1, again.Rules.Count, "the rules survived a channels save");
        Assert.AreEqual(NotificationEvent.JobFailed, again.Rules[0].Event);
    }

}
