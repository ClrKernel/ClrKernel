using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
        @"^#!(csharp|c#|fsharp|f#|pwsh|powershell|html|http|javascript|js|markdown|md|meta|mermaid|value|sql|dax|kql)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] _csharpSectionNames = { "csharp", "c#" };

    // These selectors mark sections/fences whose body is a non-C# executable
    // language; the engine routes each to its handler. We re-emit the marker so
    // the block is self-describing when it flows through execution.
    private const string _httpSelector = "#!http";
    private const string _mermaidSelector = "#!mermaid";
    private const string _pwshSelector = "#!pwsh";
    private const string _sqlSelector = "#!sql";
    private const string _daxSelector = "#!dax";
    private static readonly string[] _pwshSectionNames = { "pwsh", "powershell" };

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
            ".md" or ".markdown" => ParseMarkdown(content),
            ".csx" or ".cs" => new[] { content },
            _ => throw new NotSupportedException(
                $"#!import: unsupported file type '{extension}' (supported: .dib, .ipynb, .md, .csx, .cs)"),
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
        var section = "csharp"; // leading content defaults to C#

        void Flush() {
            var text = string.Join("\n", current).Trim();
            if (text.Length > 0) {
                if (_csharpSectionNames.Contains(section)) {
                    blocks.Add(text);
                } else if (section == "http") {
                    blocks.Add(_httpSelector + "\n" + text);
                } else if (section == "mermaid") {
                    blocks.Add(_mermaidSelector + "\n" + text);
                } else if (_pwshSectionNames.Contains(section)) {
                    blocks.Add(_pwshSelector + "\n" + text);
                } else if (section == "sql") {
                    blocks.Add(SqlBlock(text));
                } else if (section == "dax") {
                    blocks.Add(DaxBlock(text));
                }
            }
            current.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            var match = _dibSectionPattern.Match(line);
            if (match.Success) {
                Flush();
                section = match.Groups[1].Value.ToLowerInvariant();
            } else {
                current.Add(line);
            }
        }
        Flush();

        return blocks;
    }

    // Fence opener for executable markdown: ``` or ~~~ followed by an executable
    // language tag (C#, http, mermaid, or PowerShell).
    private static readonly Regex _markdownFencePattern = new(
        @"^(?<fence>`{3,}|~{3,})\s*(?<lang>csharp|c#|cs|http|mermaid|pwsh|powershell|ps1|sql|tsql|dax)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] _pwshFenceTags = { "pwsh", "powershell", "ps1" };
    private static readonly string[] _sqlFenceTags = { "sql", "tsql" };
    private static readonly string[] _daxFenceTags = { "dax" };

    // A SQL block already carrying a #!sql / #!sql-connect selector (e.g. a
    // connection-setup fence) is passed through as-is; a bare query gets the
    // #!sql selector prepended so the engine routes it.
    private static string SqlBlock(string text) =>
        text.TrimStart().StartsWith("#!sql", StringComparison.OrdinalIgnoreCase)
            ? text
            : _sqlSelector + "\n" + text;

    private static string DaxBlock(string text) =>
        text.TrimStart().StartsWith("#!dax", StringComparison.OrdinalIgnoreCase)
            ? text
            : _daxSelector + "\n" + text;

    /// <summary>
    /// Extracts executable blocks from a markdown document ("executable
    /// markdown"): fenced code blocks tagged csharp/c#/cs (C#), http (a .http
    /// request), mermaid (a diagram), or pwsh/powershell/ps1 (PowerShell) run;
    /// prose and fences with other language tags are ignored.
    /// </summary>
    public static IReadOnlyList<string> ParseMarkdown(string content) {
        var blocks = new List<string>();
        List<string> current = null;
        string closingFence = null;
        var isHttp = false;
        var isMermaid = false;
        var isPwsh = false;
        var isSql = false;
        var isDax = false;

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            if (current == null) {
                var match = _markdownFencePattern.Match(line);
                if (match.Success) {
                    current = new List<string>();
                    closingFence = match.Groups["fence"].Value;
                    isHttp = match.Groups["lang"].Value.Equals("http", StringComparison.OrdinalIgnoreCase);
                    isMermaid = match.Groups["lang"].Value.Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                    isPwsh = _pwshFenceTags.Contains(match.Groups["lang"].Value.ToLowerInvariant());
                    isSql = _sqlFenceTags.Contains(match.Groups["lang"].Value.ToLowerInvariant());
                    isDax = _daxFenceTags.Contains(match.Groups["lang"].Value.ToLowerInvariant());
                }
            } else if (line.Trim() == closingFence) {
                var text = string.Join("\n", current).Trim();
                if (text.Length > 0) {
                    blocks.Add(isHttp ? _httpSelector + "\n" + text
                        : isMermaid ? _mermaidSelector + "\n" + text
                        : isPwsh ? _pwshSelector + "\n" + text
                        : isSql ? SqlBlock(text)
                        : isDax ? DaxBlock(text)
                        : text);
                }
                current = null;
            } else {
                current.Add(line);
            }
        }

        return blocks;
    }

    /// <summary>Returns the code cells of an .ipynb document (markdown cells are skipped).</summary>
    public static IReadOnlyList<string> ParseIpynb(string content) {
        var notebook = JsonNode.Parse(content);
        var blocks = new List<string>();

        var cells = notebook?["cells"]?.AsArray() ?? new JsonArray();
        foreach (var cell in cells) {
            if (cell?["cell_type"]?.GetValue<string>() != "code") {
                continue;
            }
            var source = cell["source"];
            var code = source is JsonArray lines
                ? string.Concat(lines.Select(line => line?.GetValue<string>() ?? ""))
                : source?.GetValue<string>() ?? "";
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
