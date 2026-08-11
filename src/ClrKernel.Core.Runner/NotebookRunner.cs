using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Core.Runner;

/// <summary>
/// Executes a ClrKernel notebook headlessly. Extracts the C# cells (via
/// ClrKernel.Core.Scripting), injects the supplied parameters, and runs each cell in order
/// against a single continuing script session — the same engine the Jupyter
/// kernel and the JSON-RPC server use, so <c>#r "nuget:"</c> and <c>#!import</c>
/// behave identically.
/// </summary>
public static class NotebookRunner {
    private static readonly Regex _parametersMarker =
        new(@"^//\s*parameters\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Runs the notebook. Returns 0 on success, 1 on any cell failure.</summary>
    public static async Task<int> RunAsync(RunnerOptions options, ILoggerFactory loggerFactory) {
        var logger = loggerFactory.CreateLogger("ClrKernel.Core.Runner");

        var fullPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(fullPath)) {
            logger.LogError("Notebook not found: {Path}", fullPath);
            return 1;
        }

        var workingDir = options.WorkingDirectory != null
            ? Path.GetFullPath(options.WorkingDirectory)
            : Path.GetDirectoryName(fullPath);

        // With -o, execute into a full notebook and write it out (papermill-style).
        if (options.OutputPath != null) {
            return await RunToNotebookAsync(options, fullPath, workingDir, logger);
        }

        List<string> blocks;
        try {
            blocks = NotebookImporter.ExtractCSharpBlocks(fullPath).ToList();
        } catch (Exception e) {
            logger.LogError(e, "Could not parse notebook: {Path}", fullPath);
            return 1;
        }

        // papermill semantics: inject after the `// parameters` cell, else at the top.
        var injected = options.Parameters.RenderCell();
        var parametersCell = FindParametersCell(blocks);
        var injectIndex = parametersCell.HasValue ? parametersCell.Value + 1 : 0;

        // Route display output (Display/HTML helpers, DisplayedValue updates) to stdout.
        var previousDisplay = DisplayDataEmitter.DisplayDataHandler;
        var previousUpdate = DisplayDataEmitter.UpdateDisplayDataHandler;
        DisplayDataEmitter.DisplayDataHandler = PrintDisplay;
        DisplayDataEmitter.UpdateDisplayDataHandler = PrintDisplay;

        var engine = new InteractiveScriptEngine(workingDir, logger);

        async Task InjectIfDue(int position) {
            if (injected != null && position == injectIndex) {
                logger.LogInformation("Injecting {Count} parameter(s).", options.Parameters.Count);
                await engine.ExecuteAsync(injected);
            }
        }

        try {
            for (int i = 0; i < blocks.Count; i++) {
                await InjectIfDue(i);
                var result = await engine.ExecuteAsync(blocks[i]);
                PrintResult(result);
            }
            // Parameters cell was last (or notebook has no cells): inject at the end.
            await InjectIfDue(blocks.Count);
            return 0;
        } catch (Exception e) {
            logger.LogError(e, "Notebook execution failed.");
            return 1;
        } finally {
            DisplayDataEmitter.DisplayDataHandler = previousDisplay;
            DisplayDataEmitter.UpdateDisplayDataHandler = previousUpdate;
        }
    }

    /// <summary>
    /// Executes the notebook and writes an executed .ipynb to
    /// <see cref="RunnerOptions.OutputPath"/>. Markdown cells pass through; code
    /// cells carry their captured outputs (stdout stream, execute_result,
    /// display_data, and an error output on failure). Stops after the first
    /// failing cell (papermill semantics) but still writes the whole notebook,
    /// leaving later cells unexecuted.
    /// </summary>
    private static async Task<int> RunToNotebookAsync(
        RunnerOptions options, string fullPath, string workingDir, ILogger logger) {
        IReadOnlyList<NotebookCell> cells;
        try {
            cells = NotebookDocument.Parse(fullPath);
        } catch (Exception e) {
            logger.LogError(e, "Could not parse notebook: {Path}", fullPath);
            return 1;
        }

        // Build the run order with the injected parameters cell placed after the
        // `// parameters` cell, or at the very top when there is none.
        var injected = options.Parameters.RenderCell();
        var paramIndex = FindParametersCellIndex(cells);
        var working = new List<(NotebookCell cell, bool injected)>();
        if (injected != null && paramIndex < 0) {
            working.Add((new NotebookCell(CellKind.Code, injected.TrimEnd()), true));
        }
        for (int i = 0; i < cells.Count; i++) {
            working.Add((cells[i], false));
            if (injected != null && i == paramIndex) {
                working.Add((new NotebookCell(CellKind.Code, injected.TrimEnd()), true));
            }
        }

        var outputCells = new List<JsonObject>();
        var realStdout = Console.Out;
        var engine = new InteractiveScriptEngine(workingDir, logger);
        var previousDisplay = DisplayDataEmitter.DisplayDataHandler;
        var previousUpdate = DisplayDataEmitter.UpdateDisplayDataHandler;
        var execCount = 0;
        var exitCode = 0;
        var index = 0;

        try {
            for (; index < working.Count; index++) {
                var (cell, isInjected) = working[index];
                if (cell.Kind == CellKind.Markdown) {
                    outputCells.Add(IpynbWriter.MarkdownCell(cell.Source));
                    continue;
                }

                execCount++;
                var outputs = new JsonArray();
                var displays = new List<JsonObject>();
                var stdout = new StringBuilder();

                void OnDisplay(DisplayData d) {
                    if (d?.Data == null) {
                        return;
                    }
                    displays.Add(IpynbWriter.DisplayDataOutput(d.Data));
                    EchoDisplay(d, realStdout);
                }
                DisplayDataEmitter.DisplayDataHandler = OnDisplay;
                DisplayDataEmitter.UpdateDisplayDataHandler = OnDisplay;

                object result = null;
                Exception error = null;
                using (var consoleProxy = new ConsoleProxy(line => { stdout.AppendLine(line); realStdout.WriteLine(line); })) {
                    consoleProxy.StartRedirect();
                    try {
                        result = await engine.ExecuteAsync(cell.Source);
                    } catch (Exception e) {
                        error = e;
                    }
                }

                if (stdout.Length > 0) {
                    outputs.Add(IpynbWriter.StreamOutput("stdout", stdout.ToString()));
                }
                foreach (var display in displays) {
                    outputs.Add(display);
                }
                if (error != null) {
                    var traceback = (error.StackTrace ?? string.Empty)
                        .Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);
                    outputs.Add(IpynbWriter.ErrorOutput(error.GetType().Name, error.Message, traceback));
                    realStdout.WriteLine($"{error.GetType().Name}: {error.Message}");
                } else if (result is DisplayData rd && rd.Data is { Count: > 0 }) {
                    outputs.Add(IpynbWriter.ExecuteResultOutput(execCount, rd.Data));
                }

                outputCells.Add(IpynbWriter.CodeCell(
                    cell.Source, execCount, outputs, isInjected ? new[] { "injected-parameters" } : null));

                if (error != null) {
                    logger.LogError(error, "Cell {Index} failed; stopping.", execCount);
                    exitCode = 1;
                    index++;
                    break;
                }
            }
        } finally {
            DisplayDataEmitter.DisplayDataHandler = previousDisplay;
            DisplayDataEmitter.UpdateDisplayDataHandler = previousUpdate;
        }

        // Any cells after a failure are written unexecuted, as papermill does.
        for (; index < working.Count; index++) {
            var (cell, isInjected) = working[index];
            outputCells.Add(cell.Kind == CellKind.Markdown
                ? IpynbWriter.MarkdownCell(cell.Source)
                : IpynbWriter.CodeCell(cell.Source, null, new JsonArray(), isInjected ? new[] { "injected-parameters" } : null));
        }

        try {
            IpynbWriter.Write(options.OutputPath, outputCells);
            logger.LogInformation("Wrote executed notebook: {Path}", Path.GetFullPath(options.OutputPath));
        } catch (Exception e) {
            logger.LogError(e, "Failed to write output notebook: {Path}", options.OutputPath);
            return 1;
        }

        return exitCode;
    }

    private static int FindParametersCellIndex(IReadOnlyList<NotebookCell> cells) {
        for (int i = 0; i < cells.Count; i++) {
            if (cells[i].Kind != CellKind.Code) {
                continue;
            }
            var firstLine = cells[i].Source.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            if (_parametersMarker.IsMatch(firstLine)) {
                return i;
            }
        }
        return -1;
    }

    private static void EchoDisplay(DisplayData data, TextWriter writer) {
        if (data?.Data == null) {
            return;
        }
        if (data.Data.TryGetValue("text/plain", out var text) && text is not null) {
            var s = text.ToString();
            if (!string.IsNullOrEmpty(s)) {
                writer.WriteLine(s);
            }
        } else if (data.Data.TryGetValue("text/html", out var html) && html is not null) {
            writer.WriteLine(html.ToString());
        }
    }

    private static int? FindParametersCell(List<string> blocks) {
        for (int i = 0; i < blocks.Count; i++) {
            var firstLine = blocks[i].Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            if (_parametersMarker.IsMatch(firstLine)) {
                return i;
            }
        }
        return null;
    }

    private static void PrintResult(object result) {
        if (result is DisplayData data) {
            PrintDisplay(data);
        }
    }

    private static void PrintDisplay(DisplayData data) {
        if (data?.Data == null) {
            return;
        }
        if (data.Data.TryGetValue("text/plain", out var text) && text is not null) {
            var s = text.ToString();
            if (!string.IsNullOrEmpty(s)) {
                Console.WriteLine(s);
            }
        } else if (data.Data.TryGetValue("text/html", out var html) && html is not null) {
            Console.WriteLine(html.ToString());
        }
    }
}
