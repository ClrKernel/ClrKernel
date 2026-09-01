using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.ExtensionServer;
using ClrKernel.Core.JupyterKernel;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
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
        // Composition root: make the cell languages and the display renders
        // available to every engine this process creates, whatever mode it runs in.
        CellLanguages.RegisterDefaults();
        Formatting.Html.HtmlFormatters.RegisterDefaults();

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
            case "convert":
                return Convert(rest);
            default: {
                    // Back-compat: a kernel spec may invoke `clrkernel {connection_file}`.
                    // Treat an unknown first argument as the Jupyter connection file.
                    using var lf = CreateLoggerFactory();
                    JupyterKernelHost.Run(args, lf);
                    return 0;
                }
        }
    }

    /// <summary>
    /// `clrkernel convert notes.dib [-o notes.nb.md]` — the same document as executable
    /// markdown. Nothing is executed: this reads and rewrites, so it is safe on a
    /// notebook whose cells you have not read yet.
    /// </summary>
    private static int Convert(string[] args) {
        string input = null, output = null;
        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "-h" or "--help":
                    Console.WriteLine(_convertUsage);
                    return 0;
                case "-o" or "--output":
                    if (++i >= args.Length) {
                        Console.Error.WriteLine("-o needs a path.");
                        return 2;
                    }
                    output = args[i];
                    break;
                default:
                    if (input != null) {
                        Console.Error.WriteLine($"Unexpected extra argument: {args[i]}");
                        return 2;
                    }
                    input = args[i];
                    break;
            }
        }
        if (input == null) {
            Console.Error.WriteLine(_convertUsage);
            return 2;
        }
        if (!File.Exists(input)) {
            Console.Error.WriteLine($"No such file: {input}");
            return 2;
        }

        output ??= NotebookConverter.DefaultOutput(input);
        // Refused rather than merged or renamed: this writes a whole document, and
        // the one thing worse than not converting is quietly replacing the notebook
        // somebody had already made.
        if (File.Exists(output)) {
            Console.Error.WriteLine($"{output} already exists. Pass -o to write somewhere else.");
            return 2;
        }

        string markdown;
        try {
            markdown = NotebookConverter.ToMarkdown(
                File.ReadAllText(input),
                Path.GetExtension(input),
                CellLanguageRegistry.Default.CreateSet().Describe());
        } catch (NotSupportedException e) {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        File.WriteAllText(output, markdown);
        Console.WriteLine($"{input} -> {output}");
        return 0;
    }

    private const string _convertUsage =
        """
        Usage: clrkernel convert <notebook> [-o <output.nb.md>]

          <notebook>        A .dib, .ipynb or .csx to rewrite as executable markdown.
          -o, --output      Where to write it (default: the same name, .nb.md).
          -h, --help        Show this help.

        Nothing is executed, and stored outputs are dropped — a .nb.md carries code
        and prose, which is what makes it diff like source. Run it to get results.
        """;

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
              convert <notebook> [-o <out>]           Rewrite a .dib/.ipynb/.csx as executable
                                                      markdown (.nb.md). Nothing is executed.

              --kernel-spec-path                      Print the bundled kernel-spec directory.
              --kernel-spec-details                   Print the bundled kernel.json details.
              -h, --help                              Show this help.

            Run `clrkernel run --help` for the parameter flags, `clrkernel convert --help` for conversion.
            """);
    }
}
