using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClrKernel.Studio;

/// <summary>
/// The notification channels, read from <c>notifications.yaml</c> at the notebooks
/// root. Jobs reference channels by name in their <c>notify:</c> rules.
/// <para>
/// Repo invariant: no passwords or tokens here — only a <em>reference</em> resolved
/// at send time from the OS credential store or <c>CLRKERNEL_SECRET_*</c>, so the
/// file is safe to commit alongside the notebooks.
/// </para>
/// </summary>
public sealed class NotificationChannels {
    public const string FileName = "notifications.yaml";

    public List<ChannelConfig> Channels { get; set; } = new();

    /// <summary>
    /// When things get sent, as against where. Channels are destinations; a rule
    /// binds an event to one or more of them.
    /// <para>
    /// Same file because they are read together and a second file is a second thing
    /// to find, commit and keep in step — a rule naming a channel that moved to
    /// another file is a rule that silently stops firing.
    /// </para>
    /// </summary>
    public List<NotificationRule> Rules { get; set; } = new();

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <summary>
    /// Writes the channels file. Validates first, so an edit that would break every
    /// notification never reaches the disk, and writes via a staging file so a crash
    /// mid-write cannot truncate it.
    /// <para>
    /// There is nothing to redact here: channels hold secret <em>references</em>
    /// only, which is what keeps the file safe to commit.
    /// </para>
    /// </summary>
    public static void Save(string notebooksRoot, NotificationChannels channels) {
        var errors = channels.Validate();
        if (errors.Count > 0) {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        var path = Path.Combine(notebooksRoot, FileName);
        var staging = path + ".tmp";
        File.WriteAllText(staging, _serializer.Serialize(channels));
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>Loads the channels file, or an empty set when there is none.</summary>
    public static NotificationChannels Load(string notebooksRoot) {
        var path = Path.Combine(notebooksRoot, FileName);
        if (!File.Exists(path)) {
            return new NotificationChannels();
        }
        return _deserializer.Deserialize<NotificationChannels>(File.ReadAllText(path))
            ?? new NotificationChannels();
    }

    public ChannelConfig Find(string name) =>
        Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The rules that apply to one event in one project, in file order.</summary>
    public IEnumerable<NotificationRule> For(NotificationEvent what, string project) =>
        Rules.Where(r =>
            r.Enabled
            && r.Event == what
            // No project means every project. Scoping is per project because that
            // is what people actually mean by "tell us about ours", and per-user
            // subscriptions would need an account behind every delivery.
            && (string.IsNullOrEmpty(r.Project)
                || string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Problems worth surfacing in the UI (bad type, missing required fields).</summary>
    public IReadOnlyList<string> Validate() {
        var errors = new List<string>();
        foreach (var channel in Channels) {
            if (string.IsNullOrWhiteSpace(channel.Name)) {
                errors.Add($"{FileName}: every channel needs a name.");
                continue;
            }
            switch (channel.Type?.ToLowerInvariant()) {
                case "webhook":
                    if (string.IsNullOrWhiteSpace(channel.Url)) {
                        errors.Add($"{FileName}: webhook channel '{channel.Name}' needs a url.");
                    }
                    break;
                case "email":
                    if (string.IsNullOrWhiteSpace(channel.Host)) {
                        errors.Add($"{FileName}: email channel '{channel.Name}' needs an smtp host.");
                    }
                    if (channel.To is not { Count: > 0 }) {
                        errors.Add($"{FileName}: email channel '{channel.Name}' needs at least one 'to' address.");
                    }
                    break;
                default:
                    errors.Add($"{FileName}: channel '{channel.Name}' has unknown type '{channel.Type}' " +
                        "(expected 'webhook' or 'email').");
                    break;
            }
        }
        foreach (var rule in Rules) {
            if (rule.To is not { Count: > 0 }) {
                errors.Add($"{FileName}: the '{rule.Event}' rule has no channel to send to.");
                continue;
            }
            foreach (var name in rule.To.Where(n => Find(n) == null)) {
                // Caught here rather than at send time: a rule pointing at a channel
                // nobody has is a rule that looks configured and never arrives.
                errors.Add($"{FileName}: the '{rule.Event}' rule sends to '{name}', which is not a channel.");
            }
            if (rule.Event == NotificationEvent.RunTooSlow && rule.AfterSeconds is not > 0) {
                errors.Add($"{FileName}: a runTooSlow rule needs an afterSeconds greater than zero.");
            }
        }
        return errors;
    }
}

/// <summary>
/// What happened. Deliberately a closed set: an event nobody emits is a rule that
/// never fires, and a free-text name is how you get one by typo.
/// </summary>
public enum NotificationEvent {
    /// <summary>A run finished in any state other than Succeeded.</summary>
    JobFailed,
    /// <summary>A job succeeded whose previous run had not. The all-clear.</summary>
    JobRecovered,
    /// <summary>A run took longer than the rule's threshold.</summary>
    RunTooSlow,
    /// <summary>Something reached production, including a deletion.</summary>
    PromotedToProd,
}

/// <summary>
/// One rule: when this happens here, tell these channels.
/// </summary>
public sealed class NotificationRule {
    public NotificationEvent Event { get; set; }
    /// <summary>Empty means every project this server hosts.</summary>
    public string Project { get; set; }
    /// <summary>Empty means every branch that runs anything — usually test and prod.</summary>
    public string Environment { get; set; }
    /// <summary>Channel names. A rule with none is a rule that does nothing.</summary>
    public List<string> To { get; set; } = new();
    /// <summary>How slow is too slow, for <see cref="NotificationEvent.RunTooSlow"/>.</summary>
    public int? AfterSeconds { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>One notification channel: a generic webhook or an SMTP mailbox.</summary>
public sealed class ChannelConfig {
    public string Name { get; set; }
    /// <summary>webhook | email.</summary>
    public string Type { get; set; }

    // --- webhook ---
    public string Url { get; set; }
    /// <summary>Extra headers sent with the POST (no secrets — use bearerSecretRef).</summary>
    public Dictionary<string, string> Headers { get; set; }
    /// <summary>Secret key whose value becomes an <c>Authorization: Bearer</c> header.</summary>
    public string BearerSecretRef { get; set; }

    // --- email ---
    public string Host { get; set; }
    // Nullable so an unset value is omitted when the file is written rather than
    // stamping SMTP defaults onto, say, a webhook channel.
    public int? Port { get; set; }
    public bool? StartTls { get; set; }
    public string From { get; set; }
    public List<string> To { get; set; }
    public string User { get; set; }
    /// <summary>Secret key holding the SMTP password. Never the password itself.</summary>
    public string PasswordSecretRef { get; set; }

    [YamlIgnore]
    public bool IsWebhook => string.Equals(Type, "webhook", StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsEmail => string.Equals(Type, "email", StringComparison.OrdinalIgnoreCase);
}
