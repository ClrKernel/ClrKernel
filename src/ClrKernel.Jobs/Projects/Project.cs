using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClrKernel.Jobs;

/// <summary>
/// How a project talks to a git remote. Recorded per project because one server can
/// hold both a scratch repo with no remote at all and a repo whose remote is the
/// thing everyone else pulls from.
/// </summary>
public enum RemoteMode {
    /// <summary>No remote. Push and fetch are not offered rather than offered and failing.</summary>
    Local,
    /// <summary>A remote exists and this server's copy wins.</summary>
    ServerAuthoritative,
    /// <summary>The remote is the source of truth; a non-fast-forward push fails loudly.</summary>
    RemoteAuthoritative,
}

/// <summary>
/// One registered repo: a slug, a place on disk, and how it reaches a remote. The
/// container everything else hangs from — notebooks, jobs, run history and (later)
/// per-user branches are all scoped to one of these.
/// </summary>
public sealed class Project {
    /// <summary>Url-safe, stable, and the id used in every route and stored row.</summary>
    public string Slug { get; set; }

    /// <summary>What people call it. Free to change; the slug is not.</summary>
    public string Name { get; set; }

    /// <summary>Absolute path of the workspace — the folder holding the worktrees.</summary>
    public string Root { get; set; }

    /// <summary>
    /// The test/prod worktree layout is in use. Off means one flat folder of
    /// notebooks with no promotion workflow, which is still a legitimate project.
    /// </summary>
    public bool GitEnabled { get; set; }

    public RemoteMode RemoteMode { get; set; } = RemoteMode.Local;

    /// <summary>Remote name or url. Meaningless when <see cref="RemoteMode"/> is Local.</summary>
    public string Remote { get; set; }

    /// <summary>
    /// The <em>name</em> of a secret holding the remote's credential — never the
    /// credential. Resolved at use time from the OS credential store or
    /// <c>CLRKERNEL_SECRET_*</c>, the same rule every connection in this repo follows:
    /// a password that is written to config is a password that leaks with the config.
    /// </summary>
    public string RemoteSecret { get; set; }

    /// <summary>Whether <c>user/*</c> branches reach the remote at all.</summary>
    public bool PushUserBranches { get; set; }

    /// <summary>A copy, so a caller cannot edit the registry's own instance.</summary>
    public Project Clone() => (Project)MemberwiseClone();

    /// <summary>
    /// Turns a folder name or a display name into a slug: lowercase, and anything
    /// that is not a letter, digit or dash becomes a dash. Empty input (a name made
    /// entirely of punctuation, or a root that is a drive letter) yields "project".
    /// </summary>
    public static string SlugFor(string text) {
        var builder = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Trim()) {
            if (char.IsAsciiLetterOrDigit(c)) {
                builder.Append(char.ToLowerInvariant(c));
            } else if (builder.Length > 0 && builder[^1] != '-') {
                builder.Append('-');
            }
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length > 0 ? slug[..Math.Min(slug.Length, 60)] : "project";
    }
}

/// <summary>
/// <c>projects.json</c> in the data directory — the registered projects, or nothing
/// at all.
/// <para>
/// Absent is the normal single-project case: the registry synthesizes one project
/// from <c>--notebooks</c> and writes no file. That keeps a server that has never
/// registered anything behaving exactly as it did before projects existed, and means
/// the file, once present, is always something a person chose.
/// </para>
/// </summary>
public static class ProjectsFile {
    public const string FileName = "projects.json";

    private static readonly JsonSerializerOptions _json = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>The registered projects, or null when the file does not exist.</summary>
    public static List<Project> Read(string dataDir) {
        var path = PathIn(dataDir);
        if (!File.Exists(path)) {
            return null;
        }
        var projects = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(path), _json)
            ?? new List<Project>();
        foreach (var project in projects) {
            project.Root = Path.GetFullPath(project.Root);
        }
        return projects;
    }

    public static void Write(string dataDir, IEnumerable<Project> projects) {
        Directory.CreateDirectory(dataDir);
        var path = PathIn(dataDir);
        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(projects.ToList(), _json));
        File.Move(staging, path, overwrite: true);
    }
}
