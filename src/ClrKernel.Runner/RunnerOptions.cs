using System;
using System.Collections.Generic;
using System.IO;

namespace ClrKernel.Runner;

/// <summary>
/// Parsed arguments for <c>clrkernel run</c>. Papermill-style parameter flags:
/// <list type="bullet">
///   <item><c>-p, --parameters NAME VALUE</c> — type-inferred (bool/int/long/double/string)</item>
///   <item><c>-r, --parameters_raw NAME VALUE</c> — always a string</item>
///   <item><c>-f, --parameters_file PATH</c> — a YAML or JSON file of parameters</item>
///   <item><c>-y, --parameters_yaml YAML</c> — an inline YAML/JSON string of parameters</item>
/// </list>
/// Files and inline YAML form the base layer; <c>-p</c>/<c>-r</c> then override.
/// For any repeated name, the last value provided wins.
/// </summary>
public class RunnerOptions {
    public string InputPath { get; private set; }
    public string WorkingDirectory { get; private set; }
    public RunnerParameters Parameters { get; } = new();
    public bool HelpRequested { get; private set; }

    public const string Usage =
        """
        Usage: clrkernel run <notebook> [parameters]

          <notebook>   Path to a .nb.md, .dib, .ipynb, or .csx file to execute.

        Parameters (papermill-style):
          -p, --parameters NAME VALUE    Set a parameter, type-inferred
                                         (bool/int/long/double, else string).
          -r, --parameters_raw NAME VALUE
                                         Set a parameter as a raw string.
          -f, --parameters_file PATH     Load parameters from a YAML or JSON file.
          -y, --parameters_yaml YAML     Load parameters from an inline YAML/JSON string.
          --cwd DIR                      Working directory for the run
                                         (defaults to the notebook's directory).
          -h, --help                     Show this help.

        Files/inline YAML are the base layer; -p/-r override them. Last value wins.
        Parameters are injected after a cell whose first line is `// parameters`,
        or at the top of the notebook when there is none.
        """;

    /// <summary>Parses <c>run</c> subcommand arguments (the tokens after <c>run</c>).</summary>
    public static RunnerOptions Parse(string[] args) {
        var options = new RunnerOptions();

        // Layer file/yaml first, then -p/-r, regardless of CLI token order.
        var baseLayer = new List<Action>();   // -f, -y
        var overrideLayer = new List<Action>(); // -p, -r

        int i = 0;
        while (i < args.Length) {
            var arg = args[i];
            switch (arg) {
                case "-h":
                case "--help":
                    options.HelpRequested = true;
                    return options;

                case "-p":
                case "--parameters": {
                        var (name, value) = TakeTwo(args, ref i, arg);
                        overrideLayer.Add(() => options.Parameters.SetInferred(name, value));
                        break;
                    }
                case "-r":
                case "--parameters_raw": {
                        var (name, value) = TakeTwo(args, ref i, arg);
                        overrideLayer.Add(() => options.Parameters.SetRaw(name, value));
                        break;
                    }
                case "-f":
                case "--parameters_file": {
                        var path = TakeOne(args, ref i, arg);
                        baseLayer.Add(() => {
                            if (!File.Exists(path)) {
                                throw new FileNotFoundException($"Parameters file not found: {path}", path);
                            }
                            options.Parameters.MergeYaml(File.ReadAllText(path));
                        });
                        break;
                    }
                case "-y":
                case "--parameters_yaml": {
                        var yaml = TakeOne(args, ref i, arg);
                        baseLayer.Add(() => options.Parameters.MergeYaml(yaml));
                        break;
                    }
                case "--cwd": {
                        options.WorkingDirectory = TakeOne(args, ref i, arg);
                        break;
                    }
                default:
                    if (arg.StartsWith('-')) {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }
                    if (options.InputPath != null) {
                        throw new ArgumentException($"Unexpected extra argument: {arg}");
                    }
                    options.InputPath = arg;
                    break;
            }
            i++;
        }

        if (options.InputPath == null) {
            throw new ArgumentException("No notebook file given. See `clrkernel run --help`.");
        }

        foreach (var apply in baseLayer) {
            apply();
        }
        foreach (var apply in overrideLayer) {
            apply();
        }

        return options;
    }

    private static string TakeOne(string[] args, ref int i, string flag) {
        if (i + 1 >= args.Length) {
            throw new ArgumentException($"{flag} requires a value.");
        }
        return args[++i];
    }

    private static (string, string) TakeTwo(string[] args, ref int i, string flag) {
        if (i + 2 >= args.Length) {
            throw new ArgumentException($"{flag} requires NAME and VALUE.");
        }
        var name = args[++i];
        var value = args[++i];
        return (name, value);
    }
}
