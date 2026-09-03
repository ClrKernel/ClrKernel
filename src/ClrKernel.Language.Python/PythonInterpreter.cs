using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClrKernel.Language.Python;

/// <summary>
/// Which Python runs a cell.
///
/// <para>
/// The goal is that nobody has to install one — but "nobody has to" is not
/// "nobody may", and an air-gapped machine cannot download anything. So this is
/// an ordered search with an explicit override at the front, and every step says
/// where it looked when it finds nothing.
/// </para>
/// </summary>
public static class PythonInterpreter {
    /// <summary>Point the kernel at an interpreter of your own. The escape hatch:
    /// an air-gapped machine, a company build, or simply the one you already use.</summary>
    public const string PathVariable = "CLRKERNEL_PYTHON";

    /// <summary>Where a provisioned interpreter and its venvs live. Overridable so a
    /// container can put them on a writable volume.</summary>
    public const string HomeVariable = "CLRKERNEL_PYTHON_HOME";

    /// <summary>
    /// The interpreter to run, or null when there is none — in which case
    /// <see cref="NotFoundMessage"/> says what was tried.
    /// </summary>
    public static string Resolve() {
        var explicitPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath)) {
            // Named outright: report it even when it is wrong, rather than falling
            // through to a different interpreter than the one that was asked for.
            return explicitPath;
        }
        return Provisioned() ?? OnPath();
    }

    /// <summary>An interpreter this kernel installed, if it has. Provisioning is a
    /// separate step; this only reports what is already on disk.</summary>
    public static string Provisioned() {
        var root = Path.Combine(Home(), "interpreters");
        if (!Directory.Exists(root)) {
            return null;
        }
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "python3";
        foreach (var directory in Directory.EnumerateDirectories(root)) {
            var candidate = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(directory, name)
                : Path.Combine(directory, "bin", name);
            if (File.Exists(candidate)) {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Where provisioned interpreters and per-notebook venvs live.</summary>
    public static string Home() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } set
            ? set
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "clrkernel", "python");

    private static string OnPath() {
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator)) {
            if (directory.Length == 0) {
                continue;
            }
            foreach (var name in names) {
                string candidate;
                try {
                    candidate = Path.Combine(directory, name);
                } catch (ArgumentException) {
                    continue; // a PATH entry with invalid characters in it
                }
                if (File.Exists(candidate)) {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>What to tell somebody who has no Python, naming every place looked.</summary>
    public static string NotFoundMessage() =>
        "No Python interpreter found. The kernel looked at " + PathVariable + ", then at "
        + Path.Combine(Home(), "interpreters") + ", then on PATH. "
        + "Set " + PathVariable + " to an interpreter you already have, or let the kernel "
        + "install one for you.";
}
