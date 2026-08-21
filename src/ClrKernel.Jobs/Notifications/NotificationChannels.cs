using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClrKernel.Jobs;

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
        return errors;
    }
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
