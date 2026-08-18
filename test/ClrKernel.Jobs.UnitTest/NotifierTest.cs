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

namespace ClrKernel.Jobs.UnitTest;

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
        Directory.Delete(_root, recursive: true);
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
}
