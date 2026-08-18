using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClrKernel.Jobs;

/// <summary>
/// The on-disk model of a <c>*.jobs.yaml</c> file: an optional shared notebook,
/// shared <c>defaults</c>, and an array of jobs that inherit them. Several jobs may
/// target the same notebook with different schedules and parameters.
/// </summary>
public sealed class JobsFile {
    /// <summary>Shared notebook path, relative to the yaml file. A job may override it.</summary>
    public string Notebook { get; set; }

    public JobsFileEntry Defaults { get; set; }
    public List<JobsFileEntry> Jobs { get; set; }

    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses a jobs file and flattens each entry into a <see cref="JobDefinition"/>
    /// with the defaults merged in. Throws on yaml errors or a missing/empty jobs list.
    /// </summary>
    public static IReadOnlyList<JobDefinition> Load(string path, string notebooksRoot) {
        var file = _deserializer.Deserialize<JobsFile>(File.ReadAllText(path));
        if (file?.Jobs is not { Count: > 0 }) {
            throw new InvalidDataException("A jobs file needs a non-empty `jobs:` list.");
        }

        var defaults = file.Defaults ?? new JobsFileEntry();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return file.Jobs.Select(entry => Flatten(entry, defaults, file.Notebook, path, directory, notebooksRoot)).ToList();
    }

    private static JobDefinition Flatten(
        JobsFileEntry entry, JobsFileEntry defaults, string sharedNotebook,
        string sourceFile, string directory, string notebooksRoot) {
        if (string.IsNullOrWhiteSpace(entry.Name)) {
            throw new InvalidDataException("Every job needs a `name:`.");
        }

        var notebook = entry.Notebook ?? sharedNotebook
            ?? throw new InvalidDataException($"Job '{entry.Name}' has no notebook (set it on the job or at the top of the file).");
        var notebookPath = Path.GetFullPath(Path.Combine(directory, notebook));

        // Maps merge key-wise (entry wins); scalars and lists replace.
        var parameters = new Dictionary<string, object>(defaults.Parameters ?? new(), StringComparer.Ordinal);
        foreach (var kv in entry.Parameters ?? new()) {
            parameters[kv.Key] = kv.Value;
        }

        return new JobDefinition {
            Name = entry.Name,
            SourceFile = Path.GetFullPath(sourceFile),
            NotebookPath = notebookPath,
            NotebookRelative = Path.GetRelativePath(notebooksRoot, notebookPath).Replace('\\', '/'),
            Cron = entry.Cron ?? defaults.Cron,
            Enabled = entry.Enabled ?? defaults.Enabled ?? true,
            TimeoutSeconds = entry.TimeoutSeconds ?? defaults.TimeoutSeconds,
            RetryCount = entry.RetryCount ?? defaults.RetryCount ?? 0,
            Parameters = parameters,
            DependsOn = entry.DependsOn ?? defaults.DependsOn ?? new List<string>(),
            Notify = entry.Notify ?? defaults.Notify ?? new NotifyRules(),
        };
    }
}

/// <summary>One entry in a jobs file — also the shape of <c>defaults</c> (name-less).</summary>
public sealed class JobsFileEntry {
    public string Name { get; set; }
    public string Notebook { get; set; }
    public string Cron { get; set; }
    public bool? Enabled { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? RetryCount { get; set; }
    public Dictionary<string, object> Parameters { get; set; }
    public List<string> DependsOn { get; set; }
    public NotifyRules Notify { get; set; }
}

/// <summary>Which notification channels fire on which outcomes (channel names).</summary>
public sealed class NotifyRules {
    public List<string> OnFailure { get; set; } = new();
    public List<string> OnSuccess { get; set; } = new();
}

/// <summary>One flattened, validated job: a file entry with its defaults merged in.</summary>
public sealed class JobDefinition {
    public string Name { get; set; }
    /// <summary>Absolute path of the *.jobs.yaml this job came from.</summary>
    public string SourceFile { get; set; }
    /// <summary>Absolute path of the notebook to run.</summary>
    public string NotebookPath { get; set; }
    /// <summary>Notebook path relative to the notebooks root (display + run rows).</summary>
    public string NotebookRelative { get; set; }
    public string Cron { get; set; }
    public bool Enabled { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
    public int RetryCount { get; set; }
    public IReadOnlyDictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    public IReadOnlyList<string> DependsOn { get; set; } = new List<string>();
    public NotifyRules Notify { get; set; } = new();
}
