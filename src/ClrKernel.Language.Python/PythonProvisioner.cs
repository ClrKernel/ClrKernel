using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Language.Python;

/// <summary>
/// Gets an interpreter onto a machine that has none.
///
/// <para>
/// Via <a href="https://github.com/astral-sh/uv">uv</a>, which is one static binary
/// that installs CPython builds for every platform this kernel runs on — including
/// musl — under Apache-2.0. The alternative considered and rejected was
/// <c>Python.Included</c>: its entire payload is one
/// <c>python-…-embed-amd64.zip</c>, so it is Windows-x64 only, and elsewhere it
/// unpacks a Windows distribution and fails opaquely.
/// </para>
/// <para>
/// Nothing is embedded in the kernel. A CPython is 24 MB and uv is 35 MB, per
/// platform, against a tool that is 67 MB in total — so this downloads once, on the
/// first <c>#!python</c> cell of a machine that needs it, and caches under
/// <see cref="PythonInterpreter.Home"/>.
/// </para>
/// </summary>
public static class PythonProvisioner {
    /// <summary>Set to <c>0</c> to refuse to download anything — for an air-gapped
    /// machine, or a build that wants to fail rather than fetch.</summary>
    public const string AutoInstallVariable = "CLRKERNEL_PYTHON_AUTO_INSTALL";

    /// <summary>Pinned, not "latest": a tool that silently changes the thing it
    /// installs under people is not one they can debug.</summary>
    public const string UvVersion = "0.12.9";

    /// <summary>The CPython line installed when nothing says otherwise.</summary>
    public const string DefaultPythonVersion = "3.13";

    public static bool AutoInstallEnabled =>
        Environment.GetEnvironmentVariable(AutoInstallVariable) is not ("0" or "false" or "no");

    /// <summary>
    /// An interpreter, installing one if this machine has none and is allowed to.
    /// Returns null when there is nothing and nothing may be fetched.
    /// </summary>
    public static async Task<string> EnsureAsync(
        Action<string> log = null, CancellationToken cancellationToken = default) {
        if (PythonInterpreter.Resolve() is { } existing) {
            return existing;
        }
        if (!AutoInstallEnabled) {
            return null;
        }
        var uv = await EnsureUvAsync(log, cancellationToken).ConfigureAwait(false);
        log?.Invoke($"Installing Python {DefaultPythonVersion} (about 25 MB) — once, then cached.");
        Run(uv, new[] { "python", "install", DefaultPythonVersion });
        return PythonInterpreter.Provisioned();
    }

    /// <summary>uv itself, downloaded and checksum-verified on first use.</summary>
    private static async Task<string> EnsureUvAsync(Action<string> log, CancellationToken cancellationToken) {
        var directory = Path.Combine(PythonInterpreter.Home(), "uv", UvVersion);
        var uv = Path.Combine(directory, OperatingSystem.IsWindows() ? "uv.exe" : "uv");
        if (File.Exists(uv)) {
            return uv;
        }
        var asset = AssetName();
        var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{asset}";
        log?.Invoke($"Fetching uv {UvVersion} (about 35 MB) — once, then cached.");

        Directory.CreateDirectory(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var archive = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

        // Verified before anything is unpacked, let alone executed: this downloads a
        // binary and then runs it, which is the one place a checksum is not ceremony.
        var expected = (await http.GetStringAsync(url + ".sha256", cancellationToken)
            .ConfigureAwait(false)).Trim().Split(' ')[0];
        var actual = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) {
            throw new PythonCellException(
                $"The uv download did not match its published checksum ({url}.sha256). "
                + "Nothing was unpacked. This is either a corrupted download or something worse.");
        }

        Extract(archive, directory, asset);
        if (!File.Exists(uv)) {
            throw new PythonCellException($"uv was downloaded but {uv} is not there — the archive layout changed.");
        }
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(uv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return uv;
    }

    private static void Extract(byte[] archive, string directory, string asset) {
        using var stream = new MemoryStream(archive);
        if (asset.EndsWith(".zip", StringComparison.Ordinal)) {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries) {
                // Flattened: the archives put the binary one directory down, and the
                // name of that directory is not something to depend on.
                if (string.IsNullOrEmpty(entry.Name)) {
                    continue;
                }
                entry.ExtractToFile(Path.Combine(directory, entry.Name), overwrite: true);
            }
            return;
        }
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry) {
            var name = Path.GetFileName(entry.Name);
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || string.IsNullOrEmpty(name)) {
                continue;
            }
            entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
        }
    }

    /// <summary>The uv release asset for this machine.</summary>
    public static string AssetName() {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows()) {
            return arch switch {
                Architecture.X64 => "uv-x86_64-pc-windows-msvc.zip",
                Architecture.Arm64 => "uv-aarch64-pc-windows-msvc.zip",
                Architecture.X86 => "uv-i686-pc-windows-msvc.zip",
                _ => throw Unsupported(arch),
            };
        }
        if (OperatingSystem.IsMacOS()) {
            return arch switch {
                Architecture.Arm64 => "uv-aarch64-apple-darwin.tar.gz",
                Architecture.X64 => "uv-x86_64-apple-darwin.tar.gz",
                _ => throw Unsupported(arch),
            };
        }
        if (OperatingSystem.IsLinux()) {
            // musl rather than gnu when this is a musl system — an Alpine container
            // cannot run the gnu build, and Studio's image is the reason to care.
            var libc = IsMusl() ? "musl" : "gnu";
            return arch switch {
                Architecture.X64 => $"uv-x86_64-unknown-linux-{libc}.tar.gz",
                Architecture.Arm64 => $"uv-aarch64-unknown-linux-{libc}.tar.gz",
                _ => throw Unsupported(arch),
            };
        }
        throw new PythonCellException(
            $"No uv build for {RuntimeInformation.OSDescription}. Set {PythonInterpreter.PathVariable} "
            + "to a Python you have.");
    }

    /// <summary>
    /// Whether this Linux is musl (Alpine) rather than glibc. By the interpreter's
    /// own report, which is how .NET describes the runtime it is on — there is no
    /// portable API for it.
    /// </summary>
    private static bool IsMusl() =>
        RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
        || File.Exists("/lib/ld-musl-x86_64.so.1")
        || File.Exists("/lib/ld-musl-aarch64.so.1");

    private static PythonCellException Unsupported(Architecture arch) =>
        new($"No uv build for {arch}. Set {PythonInterpreter.PathVariable} to a Python you have.");

    private static void Run(string uv, string[] arguments) {
        var start = new ProcessStartInfo(uv) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }
        // Its own directories, so this never touches a uv the user runs themselves —
        // their interpreters, caches and settings are not the kernel's to reorganise.
        start.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(PythonInterpreter.Home(), "interpreters");
        start.Environment["UV_CACHE_DIR"] = Path.Combine(PythonInterpreter.Home(), "cache");

        using var process = Process.Start(start)
            ?? throw new PythonCellException($"Could not start {uv}.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new PythonCellException(
                $"uv {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Trim()}");
        }
    }
}
