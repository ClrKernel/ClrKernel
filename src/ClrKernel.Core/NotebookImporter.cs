using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClrKernel.Core;

/// <summary>
/// Implements the <c>#!import</c> directive: loads C# code from .dib, .ipynb,
/// .csx, or .cs files into the current REPL session. Paths resolve relative to
/// the importing file (or the notebook's working directory at the top level),
/// and each resolved file runs at most once per session unless --force is given.
/// </summary>
public class NotebookImporter {
    // Matches: #!import "path", #!import path, with optional --force before or after
    // the path. #!lib is accepted as an alias (migration compatibility with the
    // .NET Interactive-era custom directive).
    private static readonly Regex _directivePattern = new(
        @"^\s*#!(?:import|lib)\s+(?:(?<force1>--force)\s+)?(?:""(?<qpath>[^""]+)""|(?<path>[^\s""]+))(?:\s+(?<force2>--force))?\s*$",
        RegexOptions.Compiled);

    // Matches: #!import --register "name" "path" (and the #!lib alias). Registers a
    // prefix so later imports can use "name://sub/file.dib".
    private static readonly Regex _registerPattern = new(
        @"^\s*#!(?:import|lib)\s+--register\s+(?:""(?<name>[^""]+)""|(?<uname>[^\s""]+))\s+(?:""(?<path>[^""]+)""|(?<upath>[^\s""]+))\s*$",
        RegexOptions.Compiled);

    // Lines that separate sections in a .dib file. Any other #! line is cell content.
    private static readonly Regex _dibSectionPattern = new(
        @"^#!(csharp|c#|fsharp|f#|pwsh|powershell|html|http|javascript|js|markdown|md|meta|mermaid|value|sql|kql)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] _csharpSectionNames = { "csharp", "c#" };

    private readonly HashSet<string> _importedPaths = new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly Stack<string> _activePaths = new();

    private readonly Dictionary<string, string> _libPrefixes = new();

    /// <summary>Directory that relative import paths resolve against.</summary>
    public string ActivePath => _activePaths.TryPeek(out var current) ? current : Environment.CurrentDirectory;

    /// <summary>
    /// If the line is a #!import (or #!lib) directive, returns true and its parsed parts.
    /// </summary>
    public static bool TryParseDirective(string line, out string path, out bool force) {
        path = null;
        force = false;
        var match = _directivePattern.Match(line);
        if (!match.Success) {
            return false;
        }
        path = match.Groups["qpath"].Success ? match.Groups["qpath"].Value : match.Groups["path"].Value;
        force = match.Groups["force1"].Success || match.Groups["force2"].Success;
        return true;
    }

    /// <summary>
    /// If the line is a #!import --register (or #!lib --register) directive, returns
    /// true with the prefix name and base path.
    /// </summary>
    public static bool TryParseRegister(string line, out string name, out string path) {
        name = null;
        path = null;
        var match = _registerPattern.Match(line);
        if (!match.Success) {
            return false;
        }
        name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["uname"].Value;
        path = match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["upath"].Value;
        return true;
    }

    /// <summary>
    /// Registers a prefix so later imports can address files as "name://sub/file.dib".
    /// The base path is resolved (against the active import path) at registration time.
    /// </summary>
    public void RegisterPrefix(string name, string path) {
        _libPrefixes[name.Trim()] = ResolvePath(path);
    }

    /// <summary>
    /// Resolves an import path: "prefix://sub/path" resolves against the registered
    /// prefix's base path; otherwise relative paths resolve against the active import path.
    /// </summary>
    public string ResolvePath(string path) {
        string basePath, subpath;
        var separatorIndex = path.IndexOf("://", StringComparison.Ordinal);
        if (separatorIndex >= 0) {
            var prefix = path.Substring(0, separatorIndex).Trim();
            if (!_libPrefixes.TryGetValue(prefix, out basePath)) {
                throw new ArgumentException(
                    $"#!import: unknown prefix '{prefix}' — register it first with: #!import --register \"{prefix}\" \"<base-path>\"");
            }
            subpath = path.Substring(separatorIndex + 3);
        } else {
            basePath = ActivePath;
            subpath = path;
        }

        var resolved = Path.IsPathRooted(subpath) ? subpath : Path.Combine(basePath, subpath);
        return Path.GetFullPath(resolved);
    }

    /// <summary>
    /// Imports a file, executing each of its C# blocks via <paramref name="executeBlock"/>.
    /// Returns false when the file was already imported and force is not set.
    /// Nested #!import directives inside the file work because executeBlock routes
    /// back through the engine's directive-aware execution path.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> ImportAsync(
        string path, bool force, Func<string, System.Threading.Tasks.Task> executeBlock) {
        var resolvedPath = ResolvePath(path);

        if (!File.Exists(resolvedPath)) {
            throw new FileNotFoundException($"#!import: file not found: '{resolvedPath}' (from '{path}')", resolvedPath);
        }

        if (!force && _importedPaths.Contains(resolvedPath)) {
            return false;
        }

        // Parse before marking: an unsupported or unreadable file is not
        // considered imported.
        var blocks = ExtractCSharpBlocks(resolvedPath);

        // Mark before running: a failing import doesn't rerun implicitly (use --force),
        // and self/circular imports terminate instead of recursing forever.
        _importedPaths.Add(resolvedPath);

        _activePaths.Push(Path.GetDirectoryName(resolvedPath));
        try {
            foreach (var block in blocks) {
                await executeBlock(block);
            }
        } catch (Exception e) when (e is not ImportException) {
            throw new ImportException($"#!import failed in '{resolvedPath}': {e.Message}", e);
        } finally {
            _activePaths.Pop();
        }

        return true;
    }

    /// <summary>Extracts the executable C# blocks from a .dib, .ipynb, .csx, or .cs file.</summary>
    public static IReadOnlyList<string> ExtractCSharpBlocks(string resolvedPath) {
        var extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
        var content = File.ReadAllText(resolvedPath);

        return extension switch {
            ".dib" => ParseDib(content),
            ".ipynb" => ParseIpynb(content),
            ".csx" or ".cs" => new[] { content },
            _ => throw new NotSupportedException(
                $"#!import: unsupported file type '{extension}' (supported: .dib, .ipynb, .csx, .cs)"),
        };
    }

    /// <summary>
    /// Splits a .dib document into sections at kernel-selector lines (#!csharp,
    /// #!markdown, ...) and returns the C# sections. Content before the first
    /// selector is treated as C#. Magic lines that are not kernel selectors
    /// (e.g. a nested #!import) stay inside their section.
    /// </summary>
    public static IReadOnlyList<string> ParseDib(string content) {
        var blocks = new List<string>();
        var current = new List<string>();
        var currentIsCSharp = true; // leading content defaults to C#

        void Flush() {
            var text = string.Join("\n", current).Trim();
            if (currentIsCSharp && text.Length > 0) {
                blocks.Add(text);
            }
            current.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            var match = _dibSectionPattern.Match(line);
            if (match.Success) {
                Flush();
                currentIsCSharp = _csharpSectionNames.Contains(match.Groups[1].Value.ToLowerInvariant());
            } else {
                current.Add(line);
            }
        }
        Flush();

        return blocks;
    }

    /// <summary>Returns the code cells of an .ipynb document (markdown cells are skipped).</summary>
    public static IReadOnlyList<string> ParseIpynb(string content) {
        var notebook = JObject.Parse(content);
        var blocks = new List<string>();

        foreach (var cell in notebook["cells"] ?? new JArray()) {
            if ((string)cell["cell_type"] != "code") {
                continue;
            }
            var source = cell["source"];
            var code = source is JArray lines
                ? string.Concat(lines.Select(l => (string)l))
                : (string)source ?? "";
            if (code.Trim().Length > 0) {
                blocks.Add(code);
            }
        }

        return blocks;
    }
}

/// <summary>Wraps errors thrown while executing an imported file, carrying the file path context.</summary>
public class ImportException : Exception {
    public ImportException(string message, Exception inner) : base(message, inner) { }
}
