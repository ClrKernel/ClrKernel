using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClrKernel.Studio;

/// <summary>
/// Finds the <c>clrkernel</c> executable: explicit config first, then PATH, then
/// <c>~/.dotnet/tools</c> directly (a freshly installed global tool may not be on the
/// current PATH yet — same probe the VS Code extension does).
/// </summary>
public static class ClrKernelLocator {
    public static string Find(string configuredPath) {
        if (!string.IsNullOrEmpty(configuredPath)) {
            if (!File.Exists(configuredPath)) {
                throw new FileNotFoundException($"Configured clrkernel path not found: {configuredPath}", configuredPath);
            }
            return configuredPath;
        }

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "clrkernel.exe" : "clrkernel";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        var toolsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", fileName);
        if (File.Exists(toolsPath)) {
            return toolsPath;
        }

        throw new FileNotFoundException(
            "clrkernel was not found on PATH or in ~/.dotnet/tools. " +
            "Install it with: dotnet tool install --global ClrKernel");
    }
}
