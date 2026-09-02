using System;
using System.Diagnostics;

namespace ClrKernel.Core.Secrets;

internal readonly struct ProcessResult {
    public ProcessResult(int exitCode, string stdout, string stderr) {
        ExitCode = exitCode;
        StandardOutput = stdout;
        StandardError = stderr;
    }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
}

internal static class ProcessRunner {
    public static ProcessResult Run(string fileName, string[] args, string stdin = null) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        if (stdin != null) {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var outText = p.StandardOutput.ReadToEnd();
        var errText = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, outText, errText);
    }
}
