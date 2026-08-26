using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ClrKernel.Studio;

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

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <summary>Reads a jobs file without flattening (for editing it).</summary>
    public static JobsFile Read(string path) =>
        _deserializer.Deserialize<JobsFile>(File.ReadAllText(path)) ?? new JobsFile();

    /// <summary>
    /// Writes a jobs file. Round-trips through <see cref="Load"/> first so an edit
    /// that would produce an unloadable file fails before touching the disk.
    /// </summary>
    public static void Write(string path, JobsFile file, string notebooksRoot) {
        var yaml = _serializer.Serialize(file);
        var staging = path + ".tmp";
        File.WriteAllText(staging, yaml);
        try {
            Load(staging, notebooksRoot);
        } catch {
            File.Delete(staging);
            throw;
        }
        File.Move(staging, path, overwrite: true);
    }

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
            SourceFileRelative = Path.GetRelativePath(notebooksRoot, Path.GetFullPath(sourceFile)).Replace('\\', '/'),
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
    /// <summary>The project whose workspace this job was found in.</summary>
    public string Project { get; set; } = "default";
    /// <summary>test | prod, or "default" when the git workflow is off. Frozen names —
    /// they are part of the run store's keys.</summary>
    public string Environment { get; set; } = "default";
    public string Name { get; set; }
    /// <summary>Absolute path of the *.jobs.yaml this job came from.</summary>
    public string SourceFile { get; set; }
    /// <summary>That file's path relative to the notebooks root (what the API returns).</summary>
    public string SourceFileRelative { get; set; }
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

    /// <summary>
    /// A copy with different parameters, for a one-off run with overrides. The job
    /// on disk is unchanged — only this execution sees them.
    /// </summary>
    public JobDefinition With(IReadOnlyDictionary<string, object> parameters) => new() {
        Project = Project,
        Environment = Environment,
        Name = Name,
        SourceFile = SourceFile,
        SourceFileRelative = SourceFileRelative,
        NotebookPath = NotebookPath,
        NotebookRelative = NotebookRelative,
        Cron = Cron,
        Enabled = Enabled,
        TimeoutSeconds = TimeoutSeconds,
        RetryCount = RetryCount,
        Parameters = parameters,
        DependsOn = DependsOn,
        Notify = Notify,
    };
}
