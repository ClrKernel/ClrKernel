using System;
using System.Threading.Tasks;
using ClrKernel.Jupyter;
using ClrKernel.Runner;
using ClrKernel.Server;
using Microsoft.Extensions.Logging;

namespace ClrKernel;

/// <summary>
/// The ClrKernel command-line tool. Dispatches to one of three modes:
/// <c>jupyter</c> (Jupyter kernel), <c>serve</c> (stdio JSON-RPC notebook server),
/// and <c>run</c> (headless notebook runner). A bare connection-file argument is
/// still accepted for backward compatibility with kernel specs that invoke
/// <c>clrkernel {connection_file}</c>.
/// </summary>
public static class Program {
    public static async Task<int> Main(string[] args) {
        // Kernel-spec queries are terminal — answer and exit before mode dispatch.
        if (args.Length >= 1 && args[0] is "--kernel-spec-path" or "--kernel-spec-details") {
            using var kf = CreateLoggerFactory();
            JupyterKernelHost.Run(args, kf);
            return 0;
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var mode = args[0];
        var rest = args[1..];

        switch (mode) {
            case "jupyter": {
                    using var lf = CreateLoggerFactory();
                    JupyterKernelHost.Run(rest, lf);
                    return 0;
                }
            case "serve":
                // ServerHost owns stdio and its own stderr logger factory.
                await ServerHost.RunAsync();
                return 0;
            case "lsp":
                // Unified language server: LSP language features + cell execution
                // over one connection, sharing one engine.
                await LspHost.RunAsync();
                return 0;
            case "run": {
                    RunnerOptions options;
                    try {
                        options = RunnerOptions.Parse(rest);
                    } catch (Exception e) {
                        Console.Error.WriteLine(e.Message);
                        return 2;
                    }
                    if (options.HelpRequested) {
                        Console.WriteLine(RunnerOptions.Usage);
                        return 0;
                    }
                    using var lf = CreateStderrLoggerFactory();
                    return await NotebookRunner.RunAsync(options, lf);
                }
            default: {
                    // Back-compat: a kernel spec may invoke `clrkernel {connection_file}`.
                    // Treat an unknown first argument as the Jupyter connection file.
                    using var lf = CreateLoggerFactory();
                    JupyterKernelHost.Run(args, lf);
                    return 0;
                }
        }
    }

    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder => {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

    private static ILoggerFactory CreateStderrLoggerFactory() =>
        LoggerFactory.Create(builder => {
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.SetMinimumLevel(LogLevel.Information);
        });

    private static void PrintUsage() {
        Console.WriteLine(
            """
            ClrKernel — multi-language .NET notebook tool.

            Usage: clrkernel <command> [options]

            Commands:
              jupyter <connection_file> [refs_file]   Run as a Jupyter kernel.
              serve                                   Run the stdio JSON-RPC notebook server
                                                      (VS Code extension and other clients).
              lsp                                     Run the unified language server (LSP
                                                      completion/hover/signature help + execution).
              run <notebook> [parameters]             Execute a .nb.md/.dib/.ipynb/.csx notebook
                                                      headlessly, with papermill-style parameters.

              --kernel-spec-path                      Print the bundled kernel-spec directory.
              --kernel-spec-details                   Print the bundled kernel.json details.
              -h, --help                              Show this help.

            Run `clrkernel run --help` for the parameter flags.
            """);
    }
}
