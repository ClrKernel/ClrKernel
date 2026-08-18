using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClrKernel.Jobs;

/// <summary>A file or folder under the notebooks root, as the UI tree shows it.</summary>
public sealed class TreeNode {
    public string Name { get; set; }
    /// <summary>Path relative to the notebooks root, '/'-separated.</summary>
    public string Path { get; set; }
    public bool IsDirectory { get; set; }
    /// <summary>notebook | jobs | null (for directories).</summary>
    public string Kind { get; set; }
    /// <summary>Job names defined for this notebook (notebook nodes only).</summary>
    public List<string> Jobs { get; set; }
    public List<TreeNode> Children { get; set; }
}

/// <summary>
/// Browses the notebooks root: the folder tree the UI renders, and the
/// resolve-and-verify guard every path from the network must pass through.
/// </summary>
public static class NotebookTree {
    private static readonly string[] _notebookExtensions = { ".nb.md", ".ipynb", ".dib", ".csx" };

    /// <summary>
    /// Resolves a client-supplied relative path against <paramref name="root"/> and
    /// verifies the result stays inside it. Returns null for anything that escapes —
    /// absolute paths, <c>..</c> traversal, or a symlink pointing outside.
    /// </summary>
    public static string SafeResolve(string root, string relativePath) {
        if (string.IsNullOrWhiteSpace(relativePath)) {
            return null;
        }
        // Reject rooted input outright: Path.Combine would silently discard the root.
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':')) {
            return null;
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string candidate;
        try {
            candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        } catch (Exception) {
            return null;
        }

        // Resolve symlinks when the target exists, so a link out of the tree can't
        // smuggle a path past the prefix check.
        var resolved = ResolveLinks(candidate);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return resolved.Equals(fullRoot, comparison)
            || resolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            ? candidate
            : null;
    }

    private static string ResolveLinks(string path) {
        try {
            var info = File.Exists(path) ? new FileInfo(path) : (FileSystemInfo)new DirectoryInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? path));
        } catch (Exception) {
            return Path.TrimEndingDirectorySeparator(path);
        }
    }

    public static bool IsNotebook(string path) =>
        _notebookExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The notebooks/jobs-files tree under the root, with each notebook annotated
    /// with the jobs that run it. Directories with no notebooks are pruned.
    /// </summary>
    public static TreeNode Build(string root, CatalogResult catalog) {
        var jobsByNotebook = catalog.Jobs
            .GroupBy(j => j.NotebookPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(j => j.Name).OrderBy(n => n).ToList(),
                StringComparer.OrdinalIgnoreCase);
        return BuildDirectory(Path.GetFullPath(root), Path.GetFullPath(root), jobsByNotebook);
    }

    private static TreeNode BuildDirectory(
        string root, string directory, Dictionary<string, List<string>> jobsByNotebook) {
        var children = new List<TreeNode>();

        foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
            var name = Path.GetFileName(sub);
            // Skip noise that never holds notebooks.
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules") {
                continue;
            }
            var node = BuildDirectory(root, sub, jobsByNotebook);
            if (node.Children.Count > 0) {
                children.Add(node);
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            var isJobsFile = file.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase);
            if (!isJobsFile && !IsNotebook(file)) {
                continue;
            }
            children.Add(new TreeNode {
                Name = Path.GetFileName(file),
                Path = Relative(root, file),
                IsDirectory = false,
                Kind = isJobsFile ? "jobs" : "notebook",
                Jobs = !isJobsFile && jobsByNotebook.TryGetValue(file, out var jobs) ? jobs : null,
            });
        }

        return new TreeNode {
            Name = directory == root ? "/" : Path.GetFileName(directory),
            Path = directory == root ? string.Empty : Relative(root, directory),
            IsDirectory = true,
            Children = children,
        };
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
