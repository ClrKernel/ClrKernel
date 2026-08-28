using System;
using System.IO;
using System.Linq;

namespace ClrKernel.Studio;

/// <summary>
/// A <c>*.jobs.yaml</c> and the notebook it schedules are a pair, named for each
/// other: <c>etl.jobs.yaml</c> schedules <c>etl.nb.md</c> beside it.
/// <para>
/// The convention was already half here — creating a job from a notebook writes
/// the yaml at exactly this name — and making it the rule buys three things. A
/// jobs file has one notebook, so "promote this file" has one answer. Prod cannot
/// hold a schedule whose notebook is missing, because the two travel together.
/// And nothing has to read a file to know what it pairs with, which is what lets
/// a deletion be resolved when the file is already gone.
/// </para>
/// <para>
/// The base name is the file name minus its *known* extension, not everything
/// after the first dot: <c>quarterly.report.nb.md</c> pairs with
/// <c>quarterly.report.jobs.yaml</c>, which is what anybody would expect and what
/// splitting on '.' would get wrong.
/// </para>
/// </summary>
public static class JobsPairing {
    public const string JobsSuffix = ".jobs.yaml";

    /// <summary>Notebook extensions, most-expected first — the search order below.</summary>
    private static readonly string[] _notebookExtensions = { ".nb.md", ".ipynb", ".dib", ".csx" };

    public static bool IsJobsFile(string path) =>
        path != null && path.EndsWith(JobsSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>`reports/etl.jobs.yaml` → `etl`; null when it is not a jobs file.</summary>
    public static string BaseNameOfJobsFile(string path) {
        if (!IsJobsFile(path)) {
            return null;
        }
        var name = Path.GetFileName(path);
        return name[..^JobsSuffix.Length];
    }

    /// <summary>`reports/etl.nb.md` → `etl`; null when it is not a notebook.</summary>
    public static string BaseNameOfNotebook(string path) {
        if (path == null) {
            return null;
        }
        var name = Path.GetFileName(path);
        var extension = _notebookExtensions.FirstOrDefault(
            e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return extension == null ? null : name[..^extension.Length];
    }

    /// <summary>
    /// The jobs file that schedules this notebook, whether or not it exists —
    /// a path, not a promise.
    /// </summary>
    public static string JobsFileFor(string notebookPath) {
        var name = BaseNameOfNotebook(notebookPath);
        return name == null
            ? null
            : Path.Combine(Path.GetDirectoryName(notebookPath) ?? string.Empty, name + JobsSuffix);
    }

    /// <summary>
    /// The notebook this jobs file schedules, if one is beside it.
    /// <para>
    /// Existence matters here and not the other way round: a notebook has exactly
    /// one name for its jobs file, but a jobs file's notebook could be any of four
    /// extensions, so the answer is whichever is actually there. Null when none is
    /// — which is a jobs file that schedules nothing, and an error worth reporting.
    /// </para>
    /// </summary>
    public static string NotebookFor(string jobsFilePath) {
        var name = BaseNameOfJobsFile(jobsFilePath);
        if (name == null) {
            return null;
        }
        var directory = Path.GetDirectoryName(jobsFilePath) ?? string.Empty;
        return _notebookExtensions
            .Select(extension => Path.Combine(directory, name + extension))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The notebook name this jobs file must declare if it declares one at all —
    /// used to check a file-level <c>notebook:</c> agrees with the pairing rather
    /// than quietly pointing somewhere else.
    /// </summary>
    public static bool Matches(string jobsFilePath, string declared) {
        if (string.IsNullOrWhiteSpace(declared)) {
            return true;
        }
        var expected = BaseNameOfJobsFile(jobsFilePath);
        var actual = BaseNameOfNotebook(declared.Replace('\\', '/').TrimStart('.', '/'));
        return expected != null && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            // Only a sibling: `../other/etl.nb.md` has the right base name and is
            // not the notebook this file is paired with.
            && !declared.Replace('\\', '/').TrimStart('.', '/').Contains('/');
    }
}
