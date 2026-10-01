using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClrKernel.Studio;

/// <summary>
/// <c>clrkernel-studio service install|uninstall</c>: registers <c>serve</c> with
/// the Windows Service Control Manager, and removes it again.
/// <para>
/// Why a command rather than a line in the docs: the service's binPath cannot be
/// the global-tool shim (<c>clrkernel-studio.exe</c> is a launcher that execs
/// <c>dotnet</c>; the SCM would be talking to the launcher while the host that
/// answers it is the child), so it has to be <c>dotnet.exe &lt;ClrKernel.Studio.dll&gt;</c>
/// — and that dll sits under a version-numbered folder that moves on every
/// <c>dotnet tool update</c>. The running tool knows both paths; a person would
/// have to go and find them. Re-running <c>install</c> after an update rewrites
/// the registration (<c>sc config</c>) rather than failing on the existing name.
/// </para>
/// <para>
/// A service starts in <c>System32</c> with the service account's profile, so the
/// two defaults that lean on the current directory and the home folder —
/// <c>--notebooks</c> and <c>--data-dir</c> — are required here and made absolute.
/// Every other <c>serve</c> flag given is forwarded as typed.
/// </para>
/// </summary>
internal static class WindowsService {
    private static readonly string[] _own = { "service-name", "account", "password" };
    private static readonly string[] _paths = { "notebooks", "data-dir", "clrkernel", "projects-root" };

    public const string DefaultName = "ClrKernelStudio";

    internal static int Run(string verb, IReadOnlyDictionary<string, string> flags) {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            Console.Error.WriteLine("`service` registers a Windows service; on Linux use a systemd unit (docs/studio.md).");
            return 2;
        }
        var name = flags.GetValueOrDefault("service-name") ?? DefaultName;
        switch (verb) {
            case "install":
                return Install(name, flags);
            case "uninstall":
                Sc("stop", name);
                return Sc("delete", name);
            default:
                Console.Error.WriteLine("Usage: clrkernel-studio service install --notebooks <dir> --data-dir <dir> [serve options] [--service-name <name>] [--account <user> --password <pw>]\n"
                    + "       clrkernel-studio service uninstall [--service-name <name>]");
                return 2;
        }
    }

    private static int Install(string name, IReadOnlyDictionary<string, string> flags) {
        foreach (var required in new[] { "notebooks", "data-dir" }) {
            if (!flags.ContainsKey(required)) {
                Console.Error.WriteLine($"--{required} is required: a service starts in System32 under the service account, so neither the current directory nor ~ means anything useful.");
                return 2;
            }
        }
        var binPath = BinPath(Environment.ProcessPath, typeof(WindowsService).Assembly.Location, flags);
        var exists = Sc("query", name, quiet: true) == 0;
        var create = new List<string> {
            exists ? "config" : "create", name,
            "binPath=", binPath, "start=", "auto", "DisplayName=", "ClrKernel Studio",
        };
        if (flags.TryGetValue("account", out var account)) {
            create.AddRange(new[] { "obj=", account });
            if (flags.TryGetValue("password", out var password)) {
                create.AddRange(new[] { "password=", password });
            }
        }
        var rc = Sc(create.ToArray());
        if (rc != 0) {
            Console.Error.WriteLine(rc == 5
                ? "Access denied — run this from an elevated (Administrator) prompt."
                : $"sc.exe returned {rc}.");
            return rc;
        }
        Sc("description", name, "ClrKernel Studio — runs notebooks as scheduled jobs and serves the web app.");
        // Come back after a crash: 5s, then 30s, then a minute; the counter resets after a day.
        Sc("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000");
        Console.WriteLine($"{(exists ? "Updated" : "Installed")} service '{name}':");
        Console.WriteLine($"  {binPath}");
        Console.WriteLine($"Start it with `sc.exe start {name}` (or Services → ClrKernel Studio). Logs go to the Application event log.");
        return 0;
    }

    /// <summary>
    /// The command the SCM launches. <c>dotnet.exe</c> plus the dll when the tool
    /// runs framework-dependent (the global-tool case); the process itself when it
    /// is an apphost. Quoted for the SCM's own parser, which splits on spaces.
    /// </summary>
    internal static string BinPath(string processPath, string dllPath, IReadOnlyDictionary<string, string> flags) {
        var parts = new List<string>();
        // Split on both separators: this is a Windows path, and the test suite
        // runs this function on every OS.
        var host = Path.GetFileNameWithoutExtension(processPath.Split('\\', '/')[^1]);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase)) {
            parts.Add(Quote(processPath));
            parts.Add(Quote(dllPath));
        } else {
            parts.Add(Quote(processPath));
        }
        parts.Add("serve");
        foreach (var (key, value) in flags.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            if (_own.Contains(key)) {
                continue;
            }
            parts.Add("--" + key);
            parts.Add(Quote(_paths.Contains(key) ? Path.GetFullPath(value) : value));
        }
        return string.Join(" ", parts);
    }

    private static string Quote(string value) =>
        value.Any(c => c == ' ' || c == '"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static int Sc(params string[] args) => Sc(args, quiet: false);

    private static int Sc(string verb, string name, bool quiet) => Sc(new[] { verb, name }, quiet);

    private static int Sc(string[] args, bool quiet) {
        var info = new ProcessStartInfo("sc.exe") {
            UseShellExecute = false,
            RedirectStandardOutput = quiet,
            RedirectStandardError = quiet,
        };
        foreach (var arg in args) {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info);
        process.WaitForExit();
        return process.ExitCode;
    }
}
