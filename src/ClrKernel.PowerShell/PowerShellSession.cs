using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using ClrKernel.Core.Primitives;
using SMA = System.Management.Automation;

namespace ClrKernel.PowerShell;

/// <summary>
/// Hosts PowerShell in-process for a notebook session. A single persistent
/// <see cref="Runspace"/> backs every <c>#!pwsh</c> cell, so variables,
/// functions, and imported modules persist across cells — and the same runspace
/// answers completion queries, so completions reflect what the session has
/// defined. Output is captured from all streams and formatted the way the
/// PowerShell console would (via <c>Out-String</c>).
/// </summary>
public sealed class PowerShellSession : IDisposable {
    private readonly object _lock = new();
    private Runspace _runspace;
    private bool _disposed;

    private Runspace Runspace {
        get {
            if (_runspace == null) {
                // CreateDefault2 loads only Microsoft.PowerShell.Core — fast to
                // open; Import-Module still pulls anything else on demand.
                _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
                _runspace.Open();
            }
            return _runspace;
        }
    }

    /// <summary>
    /// Runs one PowerShell cell against the session runspace and returns its
    /// output (success objects formatted via Out-String, plus Write-Host,
    /// warnings, and non-terminating errors) as text/plain. A terminating error
    /// throws <see cref="PowerShellCellException"/> so the host shows it as an
    /// error output.
    /// </summary>
    public DisplayData Execute(string code) {
        lock (_lock) {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;
            ps.AddScript(code ?? string.Empty);

            Collection<PSObject> output;
            try {
                output = ps.Invoke();
            } catch (RuntimeException runtime) {
                throw new PowerShellCellException(FormatError(runtime.ErrorRecord) ?? runtime.Message, runtime);
            }

            var sb = new StringBuilder();

            // Write-Host / Write-Information appear on the Information stream.
            foreach (var record in ps.Streams.Information) {
                var text = record?.MessageData?.ToString();
                if (!string.IsNullOrEmpty(text)) {
                    sb.AppendLine(text);
                }
            }

            var formatted = FormatObjects(output);
            if (formatted.Length > 0) {
                sb.Append(formatted).Append('\n');
            }

            foreach (var warning in ps.Streams.Warning) {
                sb.Append("WARNING: ").Append(warning.Message).Append('\n');
            }
            foreach (var error in ps.Streams.Error) {
                sb.Append(FormatError(error)).Append('\n');
            }

            return new DisplayData(sb.ToString().TrimEnd('\r', '\n'));
        }
    }

    /// <summary>
    /// Returns PowerShell's native completions for <paramref name="code"/> at
    /// <paramref name="offset"/> (cmdlets, parameters, variables, provider
    /// paths, members) using the session runspace — so session-defined variables
    /// and functions are offered too.
    /// </summary>
    public PowerShellCompletion Complete(string code, int offset) {
        lock (_lock) {
            code ??= string.Empty;
            offset = Math.Max(0, Math.Min(offset, code.Length));

            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;

            var completion = CommandCompletion.CompleteInput(code, offset, null, ps);
            var result = new PowerShellCompletion {
                ReplaceStart = completion.ReplacementIndex,
                ReplaceLength = completion.ReplacementLength,
            };
            foreach (var match in completion.CompletionMatches) {
                result.Items.Add(new PowerShellCompletionItem {
                    Label = string.IsNullOrEmpty(match.ListItemText) ? match.CompletionText : match.ListItemText,
                    InsertText = match.CompletionText,
                    Kind = match.ResultType.ToString(),
                    Detail = string.IsNullOrEmpty(match.ToolTip) ? null : match.ToolTip,
                });
            }
            return result;
        }
    }

    /// <summary>
    /// Hover for the token at <paramref name="offset"/>: a command shows its
    /// type, module, synopsis, and syntax; a parameter shows its command; a
    /// variable shows its current type and value from the session. Null when
    /// there is nothing useful to show.
    /// </summary>
    public PowerShellHover Hover(string code, int offset) {
        lock (_lock) {
            code ??= string.Empty;
            offset = Math.Max(0, Math.Min(offset, code.Length));
            var ast = Parser.ParseInput(code, out _, out _);

            // Variable under the cursor: report its runtime type and value.
            var variable = ast.FindAll(a => a is VariableExpressionAst && Contains(a.Extent, offset), true)
                .OfType<VariableExpressionAst>().LastOrDefault();
            if (variable != null) {
                var markdown = DescribeVariable(variable.VariablePath.UserPath);
                if (markdown != null) {
                    return Span(markdown, variable.Extent);
                }
            }

            var command = ast.FindAll(a => a is CommandAst && Contains(a.Extent, offset), true)
                .OfType<CommandAst>().LastOrDefault();
            if (command != null) {
                var name = command.GetCommandName();

                // A parameter (-Foo) under the cursor.
                var parameter = command.CommandElements.OfType<CommandParameterAst>()
                    .FirstOrDefault(p => Contains(p.Extent, offset));
                if (parameter != null) {
                    var md = "```powershell\n-" + parameter.ParameterName + "\n```\nParameter of `" + name + "`";
                    return Span(md, parameter.Extent);
                }

                // The command name itself.
                var nameElement = command.CommandElements.FirstOrDefault();
                if (name != null && nameElement != null && Contains(nameElement.Extent, offset)) {
                    var md = DescribeCommand(name);
                    if (md != null) {
                        return Span(md, nameElement.Extent);
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Signature help for the command whose invocation contains
    /// <paramref name="offset"/>: one signature per parameter set. Null when the
    /// cursor is not inside a known command call.
    /// </summary>
    public PowerShellSignatureHelp SignatureHelp(string code, int offset) {
        lock (_lock) {
            code ??= string.Empty;
            offset = Math.Max(0, Math.Min(offset, code.Length));
            var ast = Parser.ParseInput(code, out _, out _);

            // The command being invoked: the nearest one that starts at or before
            // the cursor, not separated from it by a line break — so it matches
            // both "Get-ChildItem <cursor>" and "Get-ChildItem -Path .<cursor>".
            var command = FindEnclosingCommand(ast, code, offset);
            var name = command?.GetCommandName();
            if (name == null) {
                return null;
            }

            var info = GetCommandInfo(name);
            if (info == null) {
                return null;
            }

            var help = new PowerShellSignatureHelp();
            foreach (var set in info.ParameterSets) {
                var signature = new PowerShellSignature { Label = name + " " + set.ToString() };
                foreach (var p in set.Parameters) {
                    if (_commonParameters.Contains(p.Name)) {
                        continue;
                    }
                    signature.Parameters.Add(new PowerShellParameter { Label = "-" + p.Name });
                }
                help.Signatures.Add(signature);
            }
            return help.Signatures.Count > 0 ? help : null;
        }
    }

    private static readonly System.Collections.Generic.HashSet<string> _commonParameters =
        new(StringComparer.OrdinalIgnoreCase) {
            "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ProgressAction",
            "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer",
            "PipelineVariable", "WhatIf", "Confirm",
        };

    private static bool Contains(IScriptExtent extent, int offset) =>
        extent != null && offset >= extent.StartOffset && offset <= extent.EndOffset;

    // The command whose invocation the cursor is in: the one starting nearest at
    // or before the offset, provided no newline sits between the command and the
    // cursor (so trailing " " / "-Param " positions still resolve to it).
    private static CommandAst FindEnclosingCommand(Ast ast, string code, int offset) {
        var candidates = ast.FindAll(a => a is CommandAst, true)
            .OfType<CommandAst>()
            .Where(c => c.Extent.StartOffset <= offset)
            .OrderByDescending(c => c.Extent.StartOffset);
        foreach (var command in candidates) {
            if (offset <= command.Extent.EndOffset) {
                return command;
            }
            var end = Math.Min(offset, code.Length);
            if (command.Extent.EndOffset <= code.Length
                && code.IndexOf('\n', command.Extent.EndOffset, end - command.Extent.EndOffset) < 0) {
                return command;
            }
        }
        return null;
    }

    private static PowerShellHover Span(string markdown, IScriptExtent extent) =>
        new() { Markdown = markdown, Start = extent.StartOffset, Length = extent.EndOffset - extent.StartOffset };

    private string DescribeVariable(string name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }
        try {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;
            ps.AddCommand("Get-Variable").AddParameter("Name", name).AddParameter("ErrorAction", "Ignore");
            var variable = ps.Invoke<PSVariable>().FirstOrDefault();
            if (variable == null) {
                return null;
            }
            var value = variable.Value;
            var typeName = value?.GetType().Name ?? "null";
            var preview = value == null ? "$null" : Preview(value.ToString());
            return "```powershell\n$" + name + "  # [" + typeName + "]\n```\n" + preview;
        } catch {
            return null;
        }
    }

    private string DescribeCommand(string name) {
        var info = GetCommandInfo(name);
        if (info == null) {
            return null;
        }
        var sb = new StringBuilder();
        sb.Append("**").Append(info.CommandType).Append("** `").Append(info.Name).Append('`');
        if (!string.IsNullOrEmpty(info.ModuleName)) {
            sb.Append("  \n_").Append(info.ModuleName).Append('_');
        }
        var synopsis = GetSynopsis(name);
        if (!string.IsNullOrEmpty(synopsis)) {
            sb.Append("\n\n").Append(synopsis);
        }
        var syntax = GetSyntax(name);
        if (!string.IsNullOrEmpty(syntax)) {
            sb.Append("\n\n```powershell\n").Append(syntax).Append("\n```");
        }
        return sb.ToString();
    }

    private CommandInfo GetCommandInfo(string name) {
        try {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;
            ps.AddCommand("Get-Command").AddParameter("Name", name).AddParameter("ErrorAction", "Ignore");
            return ps.Invoke<CommandInfo>().FirstOrDefault();
        } catch {
            return null;
        }
    }

    private string GetSyntax(string name) {
        try {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;
            ps.AddScript("Get-Command -Name $args[0] -Syntax | Out-String -Width 200")
                .AddArgument(name);
            var result = ps.Invoke<string>();
            return string.Concat(result).Trim();
        } catch {
            return null;
        }
    }

    private string GetSynopsis(string name) {
        try {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = Runspace;
            ps.AddScript("(Get-Help -Name $args[0] -ErrorAction Ignore).Synopsis").AddArgument(name);
            var result = ps.Invoke<string>().FirstOrDefault();
            result = result?.Trim();
            // Help often echoes the syntax as "synopsis" when no help is installed;
            // suppress that so it isn't shown twice.
            return string.IsNullOrEmpty(result) || result.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                ? null
                : result;
        } catch {
            return null;
        }
    }

    private static string Preview(string text) {
        if (text == null) {
            return "$null";
        }
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 200 ? text.Substring(0, 200) + "…" : text;
    }

    // Formats success output objects the way the console would (tables, lists).
    private string FormatObjects(Collection<PSObject> objects) {
        if (objects == null || objects.Count == 0) {
            return string.Empty;
        }
        using var formatter = SMA.PowerShell.Create();
        formatter.Runspace = Runspace;
        formatter.AddCommand("Out-String").AddParameter("Width", 200);
        var strings = formatter.Invoke(objects);
        var sb = new StringBuilder();
        foreach (var s in strings) {
            sb.Append(s?.ToString());
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string FormatError(ErrorRecord error) {
        if (error == null) {
            return null;
        }
        var message = error.ToString();
        var position = error.InvocationInfo?.PositionMessage;
        return string.IsNullOrEmpty(position) ? message : message + "\n" + position;
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            _runspace?.Dispose();
            _runspace = null;
        }
    }
}

/// <summary>Thrown for a terminating error in a PowerShell cell.</summary>
public sealed class PowerShellCellException : Exception {
    public PowerShellCellException(string message, Exception inner) : base(message, inner) { }
}
