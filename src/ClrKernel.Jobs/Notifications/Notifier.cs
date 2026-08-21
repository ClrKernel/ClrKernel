using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ClrKernel.Jobs;

/// <summary>
/// Sends run outcomes to the channels a job names in its <c>notify:</c> rules.
/// Delivery never fails a run: a channel that errors is logged and the others
/// still go out.
/// </summary>
public class Notifier {
    private readonly JobsOptions _options;
    private readonly ILogger _logger;
    private readonly SecretStore _secrets;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Notifier(JobsOptions options, ILogger logger, SecretStore secrets = null) {
        _options = options;
        _logger = logger;
        _secrets = secrets ?? new SecretStore();
    }

    /// <summary>
    /// Notifies the channels this job wants for the run's outcome. Success rules
    /// fire on Succeeded; failure rules on everything else that finished.
    /// </summary>
    public async Task NotifyAsync(JobDefinition job, Run run, CancellationToken cancellationToken = default) {
        var wanted = run.Status == RunStatus.Succeeded
            ? job.Notify?.OnSuccess
            : job.Notify?.OnFailure;
        if (wanted is not { Count: > 0 }) {
            return;
        }

        var channels = NotificationChannels.Load(_options.NotebooksRoot);
        foreach (var name in wanted.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var channel = channels.Find(name);
            if (channel == null) {
                _logger.LogWarning(
                    "{Job}: no notification channel named '{Channel}' in {File}.",
                    job.Name, name, NotificationChannels.FileName);
                continue;
            }
            try {
                await SendAsync(channel, Message.For(job, run, _options), cancellationToken);
                _logger.LogInformation("{Job}: notified '{Channel}'.", job.Name, channel.Name);
            } catch (Exception e) {
                // A broken channel must never turn a successful run into a failure.
                _logger.LogError(e, "{Job}: notifying '{Channel}' failed.", job.Name, channel.Name);
            }
        }
    }

    /// <summary>Sends one message to one channel. Virtual so tests can observe it.</summary>
    public virtual Task SendAsync(ChannelConfig channel, Message message, CancellationToken cancellationToken = default) {
        if (channel.IsWebhook) {
            return SendWebhookAsync(channel, message, cancellationToken);
        }
        if (channel.IsEmail) {
            return SendEmailAsync(channel, message, cancellationToken);
        }
        throw new NotSupportedException(
            $"Channel '{channel.Name}' has unknown type '{channel.Type}' (expected 'webhook' or 'email').");
    }

    private async Task SendWebhookAsync(ChannelConfig channel, Message message, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Url) {
            Content = new StringContent(
                JsonSerializer.Serialize(message.Payload), Encoding.UTF8, "application/json"),
        };
        foreach (var header in channel.Headers ?? new Dictionary<string, string>()) {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (!string.IsNullOrEmpty(channel.BearerSecretRef)) {
            request.Headers.TryAddWithoutValidation(
                "Authorization", $"Bearer {ResolveSecret(channel.BearerSecretRef, channel.Name)}");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Webhook returned {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 200)}");
        }
    }

    private async Task SendEmailAsync(ChannelConfig channel, Message message, CancellationToken cancellationToken) {
        // Resolve the credential before opening a socket: a missing secret should
        // say so, not surface as a connection error against the mail server.
        var password = string.IsNullOrEmpty(channel.User)
            ? null
            : ResolveSecret(channel.PasswordSecretRef, channel.Name);

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(channel.From ?? channel.User ?? "clrkernel-jobs@localhost"));
        foreach (var to in channel.To ?? new List<string>()) {
            mail.To.Add(MailboxAddress.Parse(to));
        }
        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        // StartTls upgrades a plaintext connection; anything else negotiates by port
        // (implicit TLS on 465, plain otherwise) — SmtpClient handles the choice.
        await client.ConnectAsync(
            channel.Host, channel.Port ?? 587,
            channel.StartTls ?? true ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto,
            cancellationToken);
        if (password != null) {
            await client.AuthenticateAsync(channel.User, password, cancellationToken);
        }
        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private string ResolveSecret(string secretRef, string channelName) {
        if (string.IsNullOrEmpty(secretRef)) {
            throw new InvalidOperationException(
                $"Channel '{channelName}' needs a secret reference (passwordSecretRef / bearerSecretRef). " +
                "Passwords are never stored in config — set one with the OS credential store or " +
                "a CLRKERNEL_SECRET_* environment variable.");
        }
        if (!_secrets.TryResolve(secretRef, out var secret)) {
            throw new InvalidOperationException(
                $"Channel '{channelName}': no secret found for '{secretRef}'. " +
                $"Set CLRKERNEL_SECRET_{secretRef.ToUpperInvariant()} or store it in the OS credential store.");
        }
        return secret;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";

    /// <summary>What gets delivered: a subject/body for email, a JSON payload for webhooks.</summary>
    public sealed class Message {
        public string Subject { get; init; }
        public string Body { get; init; }
        public Dictionary<string, object> Payload { get; init; }

        public static Message For(JobDefinition job, Run run, JobsOptions options) {
            var ok = run.Status == RunStatus.Succeeded;
            var elapsed = run.StartedAt is { } start && run.FinishedAt is { } end
                ? (end - start).TotalSeconds.ToString("0.0") + "s"
                : "unknown";
            var subject = $"[ClrKernel Jobs] {job.Name} {run.Status}";
            var body = new StringBuilder()
                .AppendLine($"Job:      {job.Name}")
                .AppendLine($"Notebook: {job.NotebookRelative}")
                .AppendLine($"Status:   {run.Status}")
                .AppendLine($"Trigger:  {run.Trigger}{(run.Attempt > 1 ? $" (attempt {run.Attempt})" : string.Empty)}")
                .AppendLine($"Started:  {run.StartedAt:u}")
                .AppendLine($"Took:     {elapsed}")
                .AppendLine($"Run id:   {run.Id}")
                .AppendLine(run.ErrorSummary != null ? $"Error:    {run.ErrorSummary}" : string.Empty)
                .AppendLine($"Artifact: {run.ArtifactPath}")
                .ToString();

            return new Message {
                Subject = subject,
                Body = body,
                Payload = new Dictionary<string, object> {
                    ["job"] = job.Name,
                    ["notebook"] = job.NotebookRelative,
                    ["status"] = run.Status.ToString(),
                    ["success"] = ok,
                    ["trigger"] = run.Trigger.ToString(),
                    ["attempt"] = run.Attempt,
                    ["runId"] = run.Id.ToString(),
                    ["startedAt"] = run.StartedAt,
                    ["finishedAt"] = run.FinishedAt,
                    ["error"] = run.ErrorSummary,
                    ["artifactPath"] = run.ArtifactPath,
                    ["notebooksRoot"] = options.NotebooksRoot,
                },
            };
        }

        /// <summary>The message the channel test button sends.</summary>
        public static Message Test(string channelName) => new() {
            Subject = "[ClrKernel Jobs] test notification",
            Body = $"This is a test notification for channel '{channelName}'.\n" +
                   "If you are reading this, the channel works.\n",
            Payload = new Dictionary<string, object> {
                ["test"] = true,
                ["channel"] = channelName,
                ["message"] = "ClrKernel Jobs test notification",
            },
        };
    }
}
