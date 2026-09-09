using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// What one <c>#r "project: …"</c> line asks for.
///
/// <para>
/// The grammar is <c>project: &lt;path&gt;[, Key=Value]*</c>. Keys are
/// case-insensitive and an unknown one is an error rather than ignored — a
/// misspelled <c>Configuraton=Release</c> that silently built Debug would be the
/// jobs-file <c>scedule:</c> bug over again.
/// </para>
/// </summary>
public sealed record ProjectReferenceRequest(
    string ProjectPath, string Configuration, string Framework, bool NoBuild) {

    public const string Prefix = "project:";

    private static readonly string[] _extensions = { ".csproj", ".fsproj", ".vbproj" };

    /// <summary>
    /// The request a <c>#r</c> line makes, or null when the line is not a project
    /// reference at all. Throws when it is one and is malformed.
    /// </summary>
    /// <param name="baseDirectory">
    /// What a relative path resolves against — the notebook's directory, or the
    /// importing file's inside a <c>#!import</c>, which is the rule imports follow.
    /// </param>
    public static ProjectReferenceRequest TryParse(string line, string baseDirectory) {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("#r ", StringComparison.Ordinal)) {
            return null;
        }
        var argument = trimmed.Substring(3).Trim().TrimEnd(';').Trim().Trim('"');
        if (!argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        var parts = argument.Substring(Prefix.Length).Split(',');
        var path = parts[0].Trim();
        if (path.Length == 0) {
            throw new ArgumentException("#r \"project:\" needs a path to a project file.");
        }

        var configuration = "Debug";
        string framework = null;
        var noBuild = false;
        foreach (var option in parts.Skip(1)) {
            var pair = option.Split('=', 2);
            if (pair.Length != 2) {
                throw new ArgumentException(
                    $"#r \"project:\": '{option.Trim()}' is not Key=Value. Options are Configuration, Framework and NoBuild.");
            }
            var key = pair[0].Trim();
            var value = pair[1].Trim();
            switch (key.ToLowerInvariant()) {
                case "configuration":
                    configuration = value;
                    break;
                case "framework":
                    framework = value;
                    break;
                case "nobuild":
                    if (!bool.TryParse(value, out noBuild)) {
                        throw new ArgumentException($"#r \"project:\": NoBuild is true or false, not '{value}'.");
                    }
                    break;
                default:
                    throw new ArgumentException(
                        $"#r \"project:\": unknown option '{key}'. Options are Configuration, Framework and NoBuild.");
            }
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        if (!_extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)) {
            throw new ArgumentException(
                $"#r \"project:\": '{path}' is not a project file (.csproj, .fsproj or .vbproj). "
                + "A solution is not one — reference each project on its own line.");
        }
        if (!File.Exists(full)) {
            throw new FileNotFoundException($"#r \"project:\": no project at '{full}'.", full);
        }
        return new ProjectReferenceRequest(full, configuration, framework, noBuild);
    }
}

/// <summary>
/// One session's built projects: builds each with the .NET SDK into a private
/// output directory and says which assemblies to reference.
///
/// <para>
/// The contract is one sentence — whatever <c>dotnet build</c> produces for this
/// project, reference it. Nothing here reads a <c>.cs</c> file or interprets the
/// project beyond asking MSBuild for a few properties; source generators, T4,
/// <c>Exec</c> targets and project-to-project references all come for free because
/// MSBuild is the boundary.
/// </para>
/// </summary>
public sealed class ProjectReferences {
    /// <summary>
    /// Where builds land. Beside the NuGet restore scratch and the cell libraries,
    /// and never the project's own <c>bin/</c>: on Windows a loaded assembly is
    /// locked, and the next rebuild into the same file would fail.
    /// </summary>
    public static string StoreRoot { get; set; } = Path.Combine(Path.GetTempPath(), "clrkernel", "projects");

    /// <summary>
    /// The only thing that stops a hung build. There is no interrupt reaching the
    /// engine from any front — that is its own piece of work — so a build that
    /// never finishes is killed here rather than holding the cell for ever.
    /// </summary>
    public static TimeSpan BuildTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>What a request resolved to.</summary>
    /// <param name="AssemblyPath">The project's own output assembly.</param>
    /// <param name="ReferencePaths">Every assembly in the output the kernel does not already ship.</param>
    /// <param name="Superseded">The previous build's references, which a re-run replaces.</param>
    /// <param name="Reloaded">
    /// False when the build produced the same assembly as last time (by MVID),
    /// so the caller keeps what it has rather than referencing the same code twice.
    /// </param>
    public sealed record Result(
        string AssemblyPath, IReadOnlyList<string> ReferencePaths, IReadOnlyList<string> Superseded,
        bool Reloaded, IReadOnlyList<string> Warnings);

    private sealed record Built(int N, Guid Mvid, string AssemblyPath, string[] References);

    private readonly Dictionary<string, Built> _built = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _output;

    /// <param name="output">Where build output goes as it arrives — the cell's stdout.</param>
    public ProjectReferences(Action<string> output) {
        _output = output ?? (_ => { });
    }

    public Result Resolve(ProjectReferenceRequest request) {
        var dotnet = DotnetCli.Locate();
        var project = request.ProjectPath;

        // Two evaluations. The first is the project as written; the second pins
        // the framework, because a multi-targeted project evaluated without one
        // is the outer build, and the outer build has no TargetName.
        var targets = Evaluate(dotnet, project, "TargetFramework", "TargetFrameworks");
        var frameworks = targets["TargetFrameworks"].Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (frameworks.Length == 0) {
            frameworks = new[] { targets["TargetFramework"] };
        }
        var framework = request.Framework ?? PickFramework(frameworks, Environment.Version.Major)
            ?? throw new InvalidOperationException(
                $"#r \"project:\": {Path.GetFileName(project)} targets {string.Join(", ", frameworks)}, "
                + $"and the kernel is running .NET {Environment.Version.Major}. Add a compatible target, "
                + "or pick one with Framework=<tfm>.");
        var properties = Evaluate(dotnet, project, new[] { "TargetName", "OutputType", "AssemblyVersion" },
            $"-p:TargetFramework={framework}", $"-p:Configuration={request.Configuration}");

        var warnings = new List<string>();
        if (properties["OutputType"] is "Exe" or "WinExe") {
            warnings.Add($"{Path.GetFileName(project)} is an application; its assembly is referenced, not run.");
        }

        _built.TryGetValue(project, out var previous);
        var n = (previous?.N ?? 0) + 1;
        var outDir = Path.Combine(StoreRoot, HashOf(project), n.ToString());
        Directory.CreateDirectory(outDir);
        var assemblyName = properties["TargetName"] + ".dll";

        if (request.NoBuild) {
            // The project's own last build, copied out of its bin/ — the copy is
            // what stays loaded, so the next `dotnet build` in a terminal does not
            // find its output file locked.
            var targetPath = Evaluate(dotnet, project, new[] { "TargetPath" },
                $"-p:Configuration={request.Configuration}", $"-p:TargetFramework={framework}")["TargetPath"];
            if (!File.Exists(targetPath)) {
                throw new InvalidOperationException(
                    $"#r \"project:\": NoBuild=true, but there is no {request.Configuration}/{framework} output at "
                    + $"'{targetPath}'. Build the project first, or run this line once without NoBuild.");
            }
            CopyDirectory(Path.GetDirectoryName(targetPath), outDir);
            // A library's bin/ has no package assemblies unless the project asked
            // for them, and a missing one would surface later as a
            // FileNotFoundException from inside the first call — which reads as a
            // bug in the library. Refuse here and say what to set.
            var missing = MissingRuntimeAssemblies(
                Path.Combine(outDir, properties["TargetName"] + ".deps.json"), outDir);
            if (missing.Count > 0) {
                throw new InvalidOperationException(
                    $"#r \"project:\": NoBuild=true, but the project's output lacks {string.Join(", ", missing)}. "
                    + "A library's build does not copy its packages; set "
                    + "<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies> in the project, "
                    + "or run this line without NoBuild.");
            }
        } else {
            // Built at the version that is already loaded, so an unchanged
            // rebuild is byte-identical to it — MVID equal, nothing to do. Roslyn
            // resolves an assembly by identity, and in a script every earlier
            // submission already carries the loaded one: a rebuild with the same
            // name and version is simply ignored in favour of it, whatever its
            // path. So a change is built a second time, one revision up, which is
            // the identity Roslyn will prefer. Two builds per change, one per
            // no-change; the second is incremental and recompiles one project.
            var baseVersion = BaseVersionOf(properties["AssemblyVersion"]);
            Build(dotnet, request, framework, outDir, $"{baseVersion}.{previous?.N ?? n}");
            if (previous != null && ReadMvid(Path.Combine(outDir, assemblyName)) != previous.Mvid) {
                Build(dotnet, request, framework, outDir, $"{baseVersion}.{n}");
            }
        }

        var assemblyPath = Path.Combine(outDir, assemblyName);
        if (!File.Exists(assemblyPath)) {
            throw new InvalidOperationException(
                $"#r \"project:\": the build produced no {assemblyName} in '{outDir}'.");
        }

        // MVID rather than a timestamp: a deterministic build of unchanged source
        // gives the same module id, and a generator stamping a GUID gives a new
        // one — both of which are the right answer to "did the code change".
        var mvid = ReadMvid(assemblyPath);
        if (previous != null && previous.Mvid == mvid) {
            TryDelete(outDir);
            return new Result(previous.AssemblyPath, previous.References, Array.Empty<string>(), false, warnings);
        }

        var references = Directory.GetFiles(outDir, "*.dll")
            .Where(path => !IsShipped(path, warnings))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _built[project] = new Built(n, mvid, assemblyPath, references);
        return new Result(assemblyPath, references, previous?.References ?? Array.Empty<string>(), true, warnings);
    }

    private void Build(string dotnet, ProjectReferenceRequest request, string framework, string outDir, string version) {
        // -nodeReuse:false: a Studio kernel lives for one run, and an MSBuild
        // worker node left behind to speed up the next build outlives it.
        // CopyLocalLockFileAssemblies: a *library* build leaves its packages in
        // the NuGet cache and writes only their names to deps.json — "the output
        // directory contains the full closure" is true of applications alone.
        // Without this the project dll loads and its first call into a package
        // throws FileNotFoundException. Both are global properties, so every
        // project in the graph gets them — a private build for one session, not
        // a package.
        var (code, output) = DotnetCli.Run(dotnet, new[] {
            "build", request.ProjectPath, "-c", request.Configuration, "-f", framework, "-o", outDir,
            $"-p:AssemblyVersion={version}", "-p:CopyLocalLockFileAssemblies=true",
            "--nologo", "-v:q", "-nodeReuse:false",
        }, _output, BuildTimeout);
        if (code != 0) {
            TryDelete(outDir);
            throw new InvalidOperationException(
                $"#r \"project:\": dotnet build failed ({code}) for {request.ProjectPath}:\n{output.Trim()}");
        }
    }

    /// <summary>
    /// Runtime assemblies the project's <c>deps.json</c> names that are neither in
    /// <paramref name="directory"/> nor shipped by the kernel. Empty when there is
    /// no deps.json to read — nothing to check against is not a failure.
    /// </summary>
    public static IReadOnlyList<string> MissingRuntimeAssemblies(string depsJsonPath, string directory) {
        if (!File.Exists(depsJsonPath)) {
            return Array.Empty<string>();
        }
        var missing = new List<string>();
        using var json = JsonDocument.Parse(File.ReadAllText(depsJsonPath));
        if (!json.RootElement.TryGetProperty("targets", out var targets)) {
            return missing;
        }
        foreach (var target in targets.EnumerateObject()) {
            foreach (var library in target.Value.EnumerateObject()) {
                if (!library.Value.TryGetProperty("runtime", out var runtime)) {
                    continue;
                }
                foreach (var asset in runtime.EnumerateObject()) {
                    var name = Path.GetFileName(asset.Name);
                    if (!File.Exists(Path.Combine(directory, name)) && !IsShipped(name) && !missing.Contains(name)) {
                        missing.Add(name);
                    }
                }
            }
        }
        return missing;
    }

    /// <summary>Major.minor.build of what the project declares; the revision is this session's.</summary>
    private static string BaseVersionOf(string assemblyVersion) =>
        Version.TryParse(assemblyVersion, out var v)
            ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"
            : "1.0.0";

    /// <summary>
    /// The highest framework the running kernel can load, or null when there is
    /// none. <c>netstandard</c> always loads and ranks below every <c>netX.Y</c>;
    /// <c>net48</c>-style targets never do.
    /// </summary>
    public static string PickFramework(IEnumerable<string> frameworks, int runtimeMajor) {
        string best = null;
        var bestRank = (-1, -1);
        foreach (var tfm in frameworks) {
            var rank = Rank(tfm.Trim(), runtimeMajor);
            if (rank != null && rank.Value.CompareTo(bestRank) > 0) {
                (best, bestRank) = (tfm.Trim(), rank.Value);
            }
        }
        return best;
    }

    private static (int, int)? Rank(string tfm, int runtimeMajor) {
        if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)) {
            return (0, 0);
        }
        var match = Regex.Match(tfm, @"^net(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) {
            return null;
        }
        var major = int.Parse(match.Groups[1].Value);
        return major <= runtimeMajor ? (major, int.Parse(match.Groups[2].Value)) : null;
    }

    private static Dictionary<string, string> Evaluate(
        string dotnet, string project, string[] properties, params string[] extra) {
        var args = new List<string> { "msbuild", project, "-nologo", "-nodeReuse:false" };
        args.AddRange(properties.Select(p => "-getProperty:" + p));
        args.AddRange(extra);
        var (code, output) = DotnetCli.Run(dotnet, args.ToArray(), null, BuildTimeout);
        if (code != 0) {
            throw new InvalidOperationException(
                $"#r \"project:\": could not evaluate {project} ({code}):\n{output.Trim()}");
        }
        // One property prints bare; several print as JSON.
        if (properties.Length == 1) {
            return new Dictionary<string, string> { [properties[0]] = output.Trim() };
        }
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    private static Dictionary<string, string> Evaluate(string dotnet, string project, params string[] properties) =>
        Evaluate(dotnet, project, properties, Array.Empty<string>());

    private static readonly string _frameworkDir =
        Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;

    /// <summary>
    /// Whether the kernel already carries an assembly of this name — the shared
    /// framework's or its own. Those unify with the kernel's copy rather than being
    /// referenced twice, which Roslyn would refuse as equivalent identities. The
    /// rule is what the kernel <em>ships</em>, not what happens to be loaded so far:
    /// the latter changes as cells run, and would make the reference set depend on
    /// the order cells were executed in.
    /// </summary>
    private static bool IsShipped(string path, List<string> warnings) {
        var name = Path.GetFileName(path);
        var shipped = ShippedCopyOf(name);
        if (shipped == null) {
            return false;
        }
        var theirs = VersionOf(path);
        var ours = VersionOf(shipped);
        if (theirs != null && ours != null && theirs != ours) {
            warnings.Add(
                $"{Path.GetFileNameWithoutExtension(name)}: the project built against {theirs}; "
                + $"the kernel's {ours} is what runs.");
        }
        return true;
    }

    private static bool IsShipped(string fileName) => ShippedCopyOf(fileName) != null;

    private static string ShippedCopyOf(string fileName) =>
        new[] { _frameworkDir, AppContext.BaseDirectory }
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);

    private static Version VersionOf(string path) {
        try {
            return AssemblyName.GetAssemblyName(path).Version;
        } catch (Exception) {
            // Not a managed assembly — a native dll in the output.
            return null;
        }
    }

    private static Guid ReadMvid(string path) {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private static string HashOf(string path) {
        var normalized = OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).Substring(0, 12);
    }

    private static void CopyDirectory(string from, string to) {
        foreach (var file in Directory.GetFiles(from)) {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void TryDelete(string directory) {
        try {
            Directory.Delete(directory, recursive: true);
        } catch (Exception) {
            // A build that failed half-way can leave a file open for a moment;
            // the directory is under temp and nothing is loaded from it.
        }
    }
}

/// <summary>
/// The <c>dotnet</c> host: where it is, and running it with its output streamed.
/// </summary>
public static class DotnetCli {
    /// <summary>
    /// The kernel is itself run by <c>dotnet</c>, so the host that started this
    /// process is the surest answer — a GUI-launched VS Code on macOS has no
    /// <c>/usr/local/share/dotnet</c> on its PATH. Then <c>DOTNET_ROOT</c>, then PATH.
    /// </summary>
    public static string Locate() {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host)) {
            return host;
        }
        try {
            var self = Process.GetCurrentProcess().MainModule?.FileName;
            if (self != null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                return self;
            }
        } catch (Exception) {
            // Access to the main module can be refused; fall through.
        }
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root)) {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) {
                return candidate;
            }
        }
        return "dotnet";
    }

    /// <summary>
    /// Runs the host and returns its exit code and combined output. Each stdout
    /// line also goes to <paramref name="stream"/> as it arrives, so a build's
    /// progress shows in the cell rather than all at once at the end.
    /// </summary>
    public static (int Code, string Output) Run(
        string dotnet, string[] args, Action<string> stream, TimeSpan timeout) {
        var psi = new ProcessStartInfo {
            FileName = dotnet,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) {
            psi.ArgumentList.Add(arg);
        }
        // The kernel is a child of an editor or a scheduler; the SDK's first-run
        // banner and telemetry prompt are for a terminal.
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = new Process { StartInfo = psi };
        try {
            process.Start();
        } catch (Exception e) {
            throw new InvalidOperationException(
                "#r \"project:\" needs the .NET SDK. `dotnet` was not found — install the SDK, "
                + "or point DOTNET_ROOT at it. " + e.Message);
        }
        process.StandardInput.Close();

        // Both streams drained at once — reading one to the end while the other
        // fills its pipe deadlocks. Lines, because that is what a build prints and
        // what the cell shows; this is not a place bytes have to survive intact.
        var output = new StringBuilder();
        var gate = new object();
        async Task Drain(StreamReader reader, bool forward) {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
                lock (gate) {
                    output.AppendLine(line);
                }
                if (forward) {
                    stream?.Invoke(line);
                }
            }
        }
        var stdout = Drain(process.StandardOutput, true);
        var stderr = Drain(process.StandardError, false);

        if (!process.WaitForExit((int)timeout.TotalMilliseconds)) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
                // Exited between the check and the kill.
            }
            throw new TimeoutException(
                $"dotnet {string.Join(' ', args)} exceeded {timeout.TotalMinutes:0} minutes and was killed.");
        }
        Task.WaitAll(stdout, stderr);
        var text = output.ToString();

        // A runtime-only install has `dotnet` but no `build`: the host says the
        // command was not found, which reads as though the project were missing.
        if (process.ExitCode != 0 && text.Contains("Could not execute because the specified command", StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "#r \"project:\" needs the .NET SDK, and this machine has only the runtime — "
                + $"`{dotnet} {args[0]}` is not available. Install the SDK; in Docker, the Studio "
                + "image does not carry it.");
        }
        return (process.ExitCode, text);
    }
}
