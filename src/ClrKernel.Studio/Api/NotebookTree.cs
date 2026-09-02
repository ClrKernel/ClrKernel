using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClrKernel.Studio;

/// <summary>A file or folder under the notebooks root, as the UI tree shows it.</summary>
public sealed class TreeNode {
    public string Name { get; set; }
    /// <summary>Path relative to the notebooks root, '/'-separated.</summary>
    public string Path { get; set; }
    public bool IsDirectory { get; set; }
    /// <summary>notebook | jobs | file | null (for directories).</summary>
    public string Kind { get; set; }
    /// <summary>
    /// Whether a write to this path would be accepted on your own branch. The
    /// server's answer, not a guess from the extension — the UI opens everything
    /// and has to know which ones it may offer to change.
    /// </summary>
    public bool Editable { get; set; }
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
    /// Never written, whatever the extension says.
    ///
    /// <para>
    /// Git's own storage is the point of it: a worktree's <c>.git</c> is a *file*
    /// and the bare repo is a directory called <c>.repo.git</c>, so the check is by
    /// name rather than by kind. <c>.name.saving</c> is the staging file a save
    /// leaves behind if it crashes mid-write — half a notebook, and writing *to* it
    /// would be writing to a name the next atomic save renames over.
    /// </para>
    /// </summary>
    public static bool IsProtected(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".repo.git", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith('.') && name.EndsWith(".saving", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Never listed. Everything <see cref="IsProtected"/> covers, plus two things
    /// that are written but not browsed: <c>.scratch</c> is the query editor's own
    /// buffer — it belongs to the tool, not to the project — and <c>.DS_Store</c> is
    /// noise the operating system leaves lying around.
    /// </summary>
    public static bool IsHidden(string name) =>
        IsProtected(name)
        || name.Equals(GitService.ScratchDirectory, StringComparison.OrdinalIgnoreCase)
        || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Text files the browser edits. Deliberately generous: a file somebody
    /// expects to change and finds read-only for no reason they can see reads as
    /// a bug, and everything here is text that a text editor is the right tool for.
    /// <para>
    /// Every extension a cell language claims as a fence tag has to be in here.
    /// Those open as one runnable cell (see <c>SingleCellTag</c>), and one that
    /// opened runnable and read-only would be the worst of both.
    /// </para>
    /// </summary>
    private static readonly string[] _editableExtensions = {
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".env",
        ".txt", ".md", ".csv", ".tsv", ".log", ".rst",
        ".sql", ".tsql", ".ansisql", ".oraclesql", ".plsql", ".dax", ".mermaid", ".mmd",
        ".py", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".r", ".rb", ".lua",
        ".cs", ".fs", ".fsx", ".vb", ".java", ".go", ".rs",
        ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx", ".css", ".scss", ".html", ".htm",
        ".xml", ".xaml", ".csproj", ".props", ".targets", ".sln", ".svg", ".http",
    };

    /// <summary>
    /// Written by the server rather than by hand. Listed and readable — it is worth
    /// being able to see what the kernel will read — but an edit here is one
    /// <see cref="ConnectionMaterializer"/> deletes and rebuilds on the next change,
    /// so offering it would be offering a save that quietly comes undone.
    /// Connections are edited on the Connections page.
    /// </summary>
    public static bool IsGenerated(string name) =>
        name.Equals(ConnectionMaterializer.SharedFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ConnectionMaterializer.PrivateFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Files with no extension worth having one — matched whole.</summary>
    private static readonly string[] _editableNames = {
        ".gitignore", ".gitattributes", ".editorconfig", ".dockerignore", ".gitmodules",
        "dockerfile", "makefile", "license", "readme", "changelog",
    };

    /// <summary>
    /// What may be written on your own branch. The one definition: the tree reports
    /// it per node and <c>EditableTarget</c> enforces it, so the UI cannot come to a
    /// different conclusion from the route that refuses the save.
    /// <para>
    /// Notebooks, jobs files and text. What is left out is binary — an image opens
    /// to look at, and a <c>.dll</c> does not open at all — plus anything under a
    /// <see cref="IsProtected"/> name: this is handed a resolved absolute path, so
    /// <c>.git/config</c> arrives looking like an ordinary file with no extension.
    /// </para>
    /// </summary>
    public static bool IsEditable(string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }
        foreach (var segment in path.Split('/', '\\')) {
            if (IsProtected(segment)) {
                return false;
            }
        }
        var name = Path.GetFileName(path);
        if (IsGenerated(name)) {
            return false;
        }
        return IsNotebook(name)
            || name.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase)
            || _editableNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || _editableExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The notebooks/jobs-files tree under the root, with each notebook annotated
    /// with the jobs that run it. Directories with no notebooks are pruned.
    /// </summary>
    public static TreeNode Build(
        string root, CatalogResult catalog, string project = null, string environment = null) {
        var jobs = environment == null
            ? catalog.Jobs
            : catalog.In(project, environment);
        var jobsByNotebook = jobs
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
            // Dot-folders are listed now — a project's `.github` and `.vscode` are
            // files somebody edits. What stays out is git's own storage, the scratch
            // buffer, and build output nobody wrote by hand.
            if (IsHidden(name) || name is "bin" or "obj" or "node_modules") {
                continue;
            }
            var node = BuildDirectory(root, sub, jobsByNotebook);
            if (node.Children.Count > 0) {
                children.Add(node);
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            var name = Path.GetFileName(file);
            // Every file, not only notebooks and jobs files: this is a browser over
            // the project now, and that includes the dot-files a repo keeps at its
            // root. `.git` is one of them here — a worktree's is a file pointing at
            // the real one — which is why IsHidden is by name rather than by kind.
            if (IsHidden(name)) {
                continue;
            }
            var isJobsFile = name.EndsWith(".jobs.yaml", StringComparison.OrdinalIgnoreCase);
            var isNotebook = IsNotebook(file);
            children.Add(new TreeNode {
                Name = name,
                Path = Relative(root, file),
                IsDirectory = false,
                Kind = isJobsFile ? "jobs" : isNotebook ? "notebook" : "file",
                Editable = IsEditable(file),
                Jobs = isNotebook && jobsByNotebook.TryGetValue(file, out var jobs) ? jobs : null,
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
