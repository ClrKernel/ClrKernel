using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClrKernel.Core.Scripting;

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

    private static readonly HashSet<string> _csharpSectionNames =
        new(StringComparer.OrdinalIgnoreCase) { "csharp", "c#", "cs" };

    // .dib section names recognized as boundaries even when no registered language
    // claims them — other kernels' cells and prose, skipped by the C# extractor.
    private static readonly HashSet<string> _knownSections =
        new(StringComparer.OrdinalIgnoreCase) {
            "fsharp", "f#", "pwsh", "powershell", "html", "http", "javascript", "js", "markdown",
            "md", "meta", "mermaid", "value", "sql", "dax", "kql", "bash", "zsh", "sh", "shell",
        };

    /// <summary>
    /// Provides the language descriptors this importer routes non-C# blocks with.
    /// The engine wires this to its own live language set, so languages added
    /// mid-session are seen; unset, the process-default registry applies.
    /// </summary>
    public Func<IReadOnlyList<LanguageDescriptor>> Languages { get; set; }

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
        var blocks = ExtractCSharpBlocks(resolvedPath, Languages?.Invoke());

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

    /// <summary>Extracts the executable blocks from a .dib, .ipynb, .md, .csx, or .cs file.</summary>
    public static IReadOnlyList<string> ExtractCSharpBlocks(
        string resolvedPath, IReadOnlyList<LanguageDescriptor> languages = null) {
        var extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
        var content = File.ReadAllText(resolvedPath);

        return extension switch {
            ".dib" => ParseDib(content, languages),
            ".ipynb" => ParseIpynb(content),
            ".md" or ".markdown" => ParseMarkdown(content, languages),
            ".csx" or ".cs" => new[] { content },
            _ => throw new NotSupportedException(
                $"#!import: unsupported file type '{extension}' (supported: .dib, .ipynb, .md, .csx, .cs)"),
        };
    }

    // Null means "whatever the process default registry knows" — the historical
    // behavior of these static parsers. The engine's importer instance passes its
    // own live set instead (see Languages).
    private static IReadOnlyList<LanguageDescriptor> Resolve(IReadOnlyList<LanguageDescriptor> languages) =>
        languages ?? CellLanguageRegistry.Default.CreateSet().Describe();

    /// <summary>
    /// Splits a .dib document into sections at kernel-selector lines (#!csharp,
    /// #!markdown, ...) and returns the executable sections — C# verbatim, a
    /// registered language's section with its selector prepended. Content before
    /// the first selector is treated as C#. Magic lines that are not kernel
    /// selectors (e.g. a nested #!import) stay inside their section.
    /// </summary>
    public static IReadOnlyList<string> ParseDib(string content, IReadOnlyList<LanguageDescriptor> languages = null) {
        var byTag = LanguageDescriptor.ByTag(Resolve(languages));
        var blocks = new List<string>();
        var current = new List<string>();
        var isCSharp = true; // leading content defaults to C#
        LanguageDescriptor language = null;
        string tag = null;

        void Flush() {
            var text = string.Join("\n", current).Trim();
            if (text.Length > 0) {
                if (isCSharp) {
                    blocks.Add(text);
                } else if (language != null) {
                    blocks.Add(language.BlockForTag(tag, text));
                }
            }
            current.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            var section = DibSectionName(line);
            if (section != null &&
                (_csharpSectionNames.Contains(section) || _knownSections.Contains(section) || byTag.ContainsKey(section))) {
                Flush();
                isCSharp = _csharpSectionNames.Contains(section);
                language = isCSharp ? null : byTag.GetValueOrDefault(section);
                tag = section;
            } else {
                current.Add(line);
            }
        }
        Flush();

        return blocks;
    }

    // A bare "#!name" line is a .dib section marker; a line with arguments
    // ("#!sql-connect --name x") is a directive that belongs to its section.
    private static string DibSectionName(string line) {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("#!", StringComparison.Ordinal) || trimmed.Length <= 2) {
            return null;
        }
        var name = trimmed.Substring(2);
        return name.Any(char.IsWhiteSpace) ? null : name;
    }

    // Tagged-block opener for executable markdown: ``` or ~~~ with a language tag.
    private static readonly Regex _taggedBlockPattern = new(
        @"^(?<delim>`{3,}|~{3,})\s*(?<lang>[^\s`~]*)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts executable blocks from a markdown document ("executable
    /// markdown"): tagged code blocks tagged csharp/c#/cs (C#), http (a .http
    /// request), mermaid (a diagram), or pwsh/powershell/ps1 (PowerShell) run;
    /// prose and blocks with other language tags are ignored.
    /// </summary>
    public static IReadOnlyList<string> ParseMarkdown(string content, IReadOnlyList<LanguageDescriptor> languages = null) {
        var byTag = LanguageDescriptor.ByTag(Resolve(languages));
        var blocks = new List<string>();
        List<string> current = null;
        string closingDelimiter = null;
        var isCSharp = false;
        LanguageDescriptor language = null;
        string tag = null;

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            if (current == null) {
                var match = _taggedBlockPattern.Match(line);
                if (!match.Success) {
                    continue;
                }
                tag = match.Groups["lang"].Value;
                isCSharp = _csharpSectionNames.Contains(tag);
                language = isCSharp ? null : byTag.GetValueOrDefault(tag);
                if (isCSharp || language != null) {
                    current = new List<string>();
                    closingDelimiter = match.Groups["delim"].Value;
                }
                // Unknown-language and untagged blocks are prose: skipped entirely.
            } else if (line.Trim() == closingDelimiter) {
                var text = string.Join("\n", current).Trim();
                if (text.Length > 0) {
                    blocks.Add(language == null ? text : language.BlockForTag(tag, text));
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
