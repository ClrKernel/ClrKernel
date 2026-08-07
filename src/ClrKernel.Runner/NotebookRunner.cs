using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Primitives;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Runner;

/// <summary>
/// Executes a ClrKernel notebook headlessly. Extracts the C# cells (via
/// ClrKernel.Core), injects the supplied parameters, and runs each cell in order
/// against a single continuing script session — the same engine the Jupyter
/// kernel and the JSON-RPC server use, so <c>#r "nuget:"</c> and <c>#!import</c>
/// behave identically.
/// </summary>
public static class NotebookRunner {
    private static readonly Regex _parametersMarker =
        new(@"^//\s*parameters\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Runs the notebook. Returns 0 on success, 1 on any cell failure.</summary>
    public static async Task<int> RunAsync(RunnerOptions options, ILoggerFactory loggerFactory) {
        var logger = loggerFactory.CreateLogger("ClrKernel.Runner");

        var fullPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(fullPath)) {
            logger.LogError("Notebook not found: {Path}", fullPath);
            return 1;
        }

        var workingDir = options.WorkingDirectory != null
            ? Path.GetFullPath(options.WorkingDirectory)
            : Path.GetDirectoryName(fullPath);

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
