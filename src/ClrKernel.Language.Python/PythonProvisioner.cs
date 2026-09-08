using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    /// <summary>
    /// Replaces the base URL uv itself is fetched from, for a network that blocks
    /// github.com but proxies it through Artifactory or Nexus. The version and asset
    /// name are appended, so a mirror only has to mirror the release layout.
    /// The interpreter half needs no equivalent — uv reads
    /// <c>UV_PYTHON_INSTALL_MIRROR</c> and inherits it from this process.
    /// </summary>
    public const string UvMirrorVariable = "CLRKERNEL_PYTHON_UV_MIRROR";

    private const string _defaultUvBaseUrl = "https://github.com/astral-sh/uv/releases/download";

    /// <summary>Matches the <c>HttpClient</c> timeout on the other half of this.</summary>
    private static readonly TimeSpan _installTimeout = TimeSpan.FromMinutes(10);

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
        await RunAsync(uv, new[] { "python", "install", DefaultPythonVersion }, log, cancellationToken)
            .ConfigureAwait(false);
        return PythonInterpreter.Provisioned();
    }

    /// <summary>
    /// uv itself, downloaded and checksum-verified on first use. Shared with
    /// <see cref="PythonEnvironment"/>, which needs it to install packages even on a
    /// machine that already had a Python and so never provisioned one.
    /// </summary>
    internal static async Task<string> EnsureUvAsync(Action<string> log, CancellationToken cancellationToken) {
        if (!AutoInstallEnabled && !File.Exists(Path.Combine(
                PythonInterpreter.Home(), "uv", UvVersion, OperatingSystem.IsWindows() ? "uv.exe" : "uv"))) {
            throw new PythonCellException(
                $"This needs uv and there is none cached, but {AutoInstallVariable} forbids downloading it. "
                + EscapeHatches());
        }

        var directory = Path.Combine(PythonInterpreter.Home(), "uv", UvVersion);
        var uv = Path.Combine(directory, OperatingSystem.IsWindows() ? "uv.exe" : "uv");
        if (File.Exists(uv)) {
            return uv;
        }
        // ponytail: two notebooks opening at once on a cold machine can both unpack
        // here, which is a sharing violation on Windows. Cross-process, so no lock in
        // this process would help; a lock file is the upgrade if it is ever seen.
        var asset = AssetName();
        var url = UvUrl(asset);
        log?.Invoke($"Fetching uv {UvVersion} (about 35 MB) — once, then cached.");

        Directory.CreateDirectory(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        byte[] archive;
        string published;
        try {
            archive = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            published = await http.GetStringAsync(url + ".sha256", cancellationToken).ConfigureAwait(false);
        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
            throw new PythonCellException(Unreachable(url, e.Message), e);
        }

        // Verified before anything is unpacked, let alone executed: this downloads a
        // binary and then runs it, which is the one place a checksum is not ceremony.
        var expected = ChecksumIn(published);
        if (expected == null) {
            // A filter that answers 200 with a block page rather than refusing the
            // connection lands here, and "corrupt download" would be the wrong thing
            // to go looking at.
            throw new PythonCellException(
                $"{url}.sha256 did not return a checksum. Something on the network answered "
                + "instead of the server — a proxy, a captive portal or a content filter. "
                + EscapeHatches());
        }
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

    /// <summary>Where uv is fetched from, honouring <see cref="UvMirrorVariable"/>.</summary>
    public static string UvUrl(string asset) {
        var mirror = Environment.GetEnvironmentVariable(UvMirrorVariable);
        var baseUrl = string.IsNullOrWhiteSpace(mirror) ? _defaultUvBaseUrl : mirror.TrimEnd('/');
        return $"{baseUrl}/{UvVersion}/{asset}";
    }

    /// <summary>
    /// The SHA-256 digest in a published checksum file, or null when the body is not
    /// one — an HTML page from whatever intercepted the request, most likely.
    /// </summary>
    /// <remarks>
    /// Any whitespace-separated 64-hex token, so both <c>&lt;hash&gt;  &lt;file&gt;</c>
    /// (coreutils, what GitHub publishes) and <c>SHA256 (file) = &lt;hash&gt;</c> (BSD)
    /// read the same. A mirror that reformats is not a network attack, and reporting
    /// it as one sends people to exactly the wrong place.
    ///
    /// ponytail: a mirror serving a combined <c>SHA256SUMS</c> at this URL would give
    /// the first hash in it, not this asset's, and fail the comparison — safe, and
    /// still the wrong explanation. Match the asset name if that ever shows up; the
    /// per-asset URL shape means it has not.
    /// </remarks>
    public static string ChecksumIn(string body) =>
        body.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Length == 64 && t.All(Uri.IsHexDigit));

    /// <summary>
    /// Whether uv failed because it did not trust the certificate it was shown.
    ///
    /// <para>
    /// uv validates against bundled Mozilla roots, not the platform store, so a
    /// network that re-signs TLS breaks it on exactly the machine where NuGet works
    /// — the corporate root is in the OS store, which is the one place uv does not
    /// look. Matched on the message because uv reports every network failure as the
    /// same exit code.
    /// </para>
    /// </summary>
    public static bool LooksLikeCertificateFailure(string error) =>
        error.Contains("certificate", StringComparison.OrdinalIgnoreCase)
        || error.Contains("self-signed", StringComparison.OrdinalIgnoreCase)
        || error.Contains("self signed", StringComparison.OrdinalIgnoreCase)
        || error.Contains("UnknownIssuer", StringComparison.OrdinalIgnoreCase);

    private static string EscapeHatches() =>
        $"Set {PythonInterpreter.PathVariable} to a Python you already have, or "
        + $"{UvMirrorVariable} and UV_PYTHON_INSTALL_MIRROR to an internal mirror, or "
        + $"{AutoInstallVariable}=0 to stop the kernel trying.";

    private static string Unreachable(string url, string detail) =>
        $"Could not download uv from {url}: {detail} "
        + "The usual cause is a network that does not allow github.com. " + EscapeHatches();

    internal static async Task<string> RunAsync(
        string uv, string[] arguments, Action<string> log, CancellationToken cancellationToken) {
        // One budget for both attempts, not one each: a certificate failure followed
        // by a hang would otherwise be twice the ceiling this is documented as having.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_installTimeout);

        var (code, error, output) = await ExecuteAsync(uv, arguments, false, deadline.Token, cancellationToken)
            .ConfigureAwait(false);
        if (code != 0 && LooksLikeCertificateFailure(error)) {
            // Second attempt, not the default: the platform store is right on a machine
            // that re-signs TLS and wrong on a container that has no store at all, and
            // only one of those two tells you which it is up front.
            log?.Invoke(
                "uv did not trust the TLS certificate it was shown — retrying against this "
                + "machine's own certificate store.");
            (code, error, output) = await ExecuteAsync(uv, arguments, true, deadline.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        if (code != 0) {
            throw new PythonCellException(
                $"uv {string.Join(' ', arguments)} failed ({code}): {error.Trim()} " + EscapeHatches());
        }
        // uv reports what it resolved on stderr, not stdout; both, so neither is lost.
        return string.Concat(output, error).Trim();
    }

    /// <param name="deadline">Stops the child; shared across both attempts.</param>
    /// <param name="cancellationToken">Only to tell a cancellation from a timeout.</param>
    private static async Task<(int ExitCode, string Error, string Output)> ExecuteAsync(
        string uv, string[] arguments, bool systemCerts,
        CancellationToken deadline, CancellationToken cancellationToken) {
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
        // Everything else — HTTP_PROXY, UV_PYTHON_INSTALL_MIRROR, SSL_CERT_FILE — is
        // inherited, which is how a proxied network works with no code at all.
        start.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(PythonInterpreter.Home(), "interpreters");
        start.Environment["UV_CACHE_DIR"] = Path.Combine(PythonInterpreter.Home(), "cache");
        if (systemCerts) {
            // The spelling uv 0.12 wants; it warns loudly about the older UV_NATIVE_TLS,
            // and the version is pinned, so there is no second name to hedge with.
            start.Environment["UV_SYSTEM_CERTS"] = "true";
        }

        using var process = Process.Start(start)
            ?? throw new PythonCellException($"Could not start {uv}.");

        // Both pipes drained concurrently: reading one to completion first deadlocks
        // if the child fills the other, and stdout has to stay redirected because in
        // serve and lsp stdout is the protocol.
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);


        // A firewall that drops packets rather than refusing them is the one failure
        // that otherwise returns nothing, ever: uv retries into a black hole and the
        // cell hangs with no interrupt. Same ceiling as the HttpClient half.
        //
        // ponytail: the ceiling is the real protection, not the token —
        // ICellExecutionContext carries no CancellationToken, so nothing upstream can
        // pass one yet, and a host interrupt is a process-tree kill of the whole
        // kernel (which takes uv with it). Honour the token anyway, so it starts
        // working the day the cell contract grows one.
        try {
            await process.WaitForExitAsync(deadline).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
                // Already gone, or not ours to kill; the throw below is the report.
            }
            // Let the two readers finish against the now-closed pipes rather than
            // leaving them to fault against a disposed process.
            try {
                await Task.WhenAll(error, output).ConfigureAwait(false);
            } catch (Exception) {
                // Nothing here is worth reporting over the timeout itself.
            }
            throw new PythonCellException(
                cancellationToken.IsCancellationRequested
                    ? $"uv {string.Join(' ', arguments)} was cancelled."
                    : $"uv {string.Join(' ', arguments)} did not finish within "
                        + $"{_installTimeout.TotalMinutes:0} minutes and was stopped. A network that "
                        + "drops connections rather than refusing them looks like this. " + EscapeHatches());
        }
        return (process.ExitCode, await error.ConfigureAwait(false), await output.ConfigureAwait(false));
    }
}
