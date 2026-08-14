using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using SMA = System.Management.Automation;

namespace ClrKernel.Language.PowerShell;

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

    // Named PSRemoting targets (#!pwsh-connect / connections.json) and their live
    // runspaces, opened lazily. A remote runspace persists like the local one, so
    // remote state carries across cells naturally.
    private readonly Dictionary<string, PwshConnectionSpec> _remoteSpecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Runspace> _remoteRunspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecretStore _secrets = new();

    /// <summary>Registers a PSRemoting target from a <c>#!pwsh-connect</c> line.</summary>
    public PwshConnectionSpec Connect(string directiveLine) {
        var spec = PwshDirectives.ParseConnect(directiveLine);
        lock (_lock) {
            _remoteSpecs[spec.Name] = spec;
            if (_remoteRunspaces.Remove(spec.Name, out var stale)) {
                stale.Dispose(); // a redefinition reconnects on next use
            }
        }
        return spec;
    }

    /// <summary>Registers every <c>"$type": "PSRemoting"</c> and <c>"$type": "Ssh"</c>
    /// entry from the nearest connections.json (+ .local overlay); one SSH host
    /// definition serves both shell and PowerShell cells.</summary>
    public IReadOnlyList<string> LoadFromConfig(string startDirectory = null) {
        var loaded = new List<string>();
        foreach (var file in ClrKernel.Database.ConnectionConfig.FindFiles(startDirectory)) {
            foreach (var node in ClrKernel.Database.ConnectionConfig.LoadAllRaw(file)) {
                if (!node.IsType(PwshConnectionConfig.TypeName) && !node.IsType(PwshConnectionConfig.SshTypeName)) {
                    continue;
                }
                var spec = PwshConnectionConfig.FromNode(node);
                lock (_lock) {
                    _remoteSpecs[spec.Name] = spec;
                }
                if (!loaded.Contains(node.Name)) {
                    loaded.Add(node.Name);
                }
            }
        }
        return loaded;
    }

    private Runspace RunspaceFor(string connectionName) {
        if (string.IsNullOrEmpty(connectionName)) {
            return Runspace;
        }
        lock (_lock) {
            if (_remoteRunspaces.TryGetValue(connectionName, out var open)) {
                return open;
            }
        }
        if (!HasSpec(connectionName)) {
            LoadFromConfig(); // a saved target may not have been loaded yet
        }
        PwshConnectionSpec spec;
        lock (_lock) {
            if (!_remoteSpecs.TryGetValue(connectionName, out spec)) {
                throw new PowerShellCellException(
                    $"No PSRemoting connection named '{connectionName}'. " +
                    (_remoteSpecs.Count == 0
                        ? "Add one with #!pwsh-connect --name <n> --host <host>."
                        : $"Known connections: {string.Join(", ", _remoteSpecs.Keys)}."));
            }
        }
        var runspace = RunspaceFactory.CreateRunspace(spec.CreateConnectionInfo(_secrets));
        try {
            // SSHConnectionInfo locates the ssh binary through PowerShell's command
            // discovery, which reads an execution context from thread-local state —
            // a hosted SDK thread has none, so the open dies with ENOENT before
            // touching the network. The session's local runspace, set as this
            // thread's DefaultRunspace for the duration of the open, provides it.
            var previous = Runspace.DefaultRunspace;
            System.Management.Automation.Runspaces.Runspace.DefaultRunspace = Runspace;
            try {
                runspace.Open();
            } finally {
                System.Management.Automation.Runspaces.Runspace.DefaultRunspace = previous;
            }
        } catch (Exception e) {
            runspace.Dispose();
            throw new PowerShellCellException(
                $"Could not open a remote runspace on {spec.Describe()}: {e.Message}" +
                (spec.Transport == PwshTransport.Ssh
                    ? " (PowerShell-over-SSH needs PowerShell installed on the remote and the ssh subsystem enabled.)"
                    : ""), e);
        }
        EnableAnsiOutput(runspace);
        lock (_lock) {
            // Another thread may have raced the open; keep the first, drop ours.
            if (_remoteRunspaces.TryGetValue(connectionName, out var existing)) {
                runspace.Dispose();
                return existing;
            }
            _remoteRunspaces[connectionName] = runspace;
            return runspace;
        }
    }

    private bool HasSpec(string name) {
        lock (_lock) {
            return _remoteSpecs.ContainsKey(name);
        }
    }

    private Runspace Runspace {
        get {
            if (_runspace == null) {
                // CreateDefault2 loads only Microsoft.PowerShell.Core — fast to
                // open; Import-Module still pulls anything else on demand.
                _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
                _runspace.Open();
                EnableAnsiOutput(_runspace);
            }
            return _runspace;
        }
    }

    /// <summary>
    /// Makes PowerShell colour its own output.
    /// </summary>
    /// <remarks>
    /// <c>$PSStyle.OutputRendering</c> defaults to <c>Host</c>, meaning "emit escape sequences only
    /// if the host is a console that can show them". A hosted runspace is not, so every table,
    /// list and formatted view came through with the colour silently dropped — not stripped later,
    /// never produced. <c>Ansi</c> makes it always emit, which is what a notebook wants: the cell
    /// renders the escapes as HTML, and the plain-text view strips them again.
    /// <para>Best effort — <c>$PSStyle</c> arrived in PowerShell 7.2.</para>
    /// </remarks>
    private static void EnableAnsiOutput(Runspace runspace) {
        try {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("if ($null -ne $PSStyle) { $PSStyle.OutputRendering = 'Ansi' }");
            ps.Invoke();
        } catch (RuntimeException) {
            // An older PowerShell simply stays monochrome.
        }
    }

    /// <summary>
    /// Runs one PowerShell cell against the session runspace and returns its
    /// output (success objects formatted via Out-String, plus Write-Host,
    /// warnings, and non-terminating errors) as text/plain. A terminating error
    /// throws <see cref="PowerShellCellException"/> so the host shows it as an
    /// error output.
    /// </summary>
    public DisplayData Execute(string code, string connectionName = null) {
        var runspace = RunspaceFor(connectionName);
        lock (_lock) {
            using var ps = SMA.PowerShell.Create();
            ps.Runspace = runspace;
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
                // Write-Host carries its colours as properties on a HostInformationMessage rather
                // than as escape sequences — a console host applies them itself. Turning them back
                // into ANSI is what keeps `Write-Host -ForegroundColor Red` red in a notebook.
                // (A -BackgroundColor with no foreground never arrives: PowerShell delivers the
                // record with both colours null in that case, so there is nothing to apply.)
                if (record?.MessageData is HostInformationMessage host) {
                    var text = host.Message;
                    if (!string.IsNullOrEmpty(text)) {
                        sb.AppendLine(Colourize(text, host.ForegroundColor, host.BackgroundColor));
                    }
                    continue;
                }
                var plain = record?.MessageData?.ToString();
                if (!string.IsNullOrEmpty(plain)) {
                    sb.AppendLine(plain);
                }
            }

            var formatted = FormatObjects(output);
            if (formatted.Length > 0) {
                sb.Append(formatted).Append('\n');
            }

            // The console colours these; we format them ourselves, so we colour them ourselves.
            foreach (var warning in ps.Streams.Warning) {
                sb.Append(Colourize("WARNING: " + warning.Message, ConsoleColor.Yellow, null)).Append('\n');
            }
            foreach (var error in ps.Streams.Error) {
                sb.Append(Colourize(FormatError(error), ConsoleColor.Red, null)).Append('\n');
            }

            var console = sb.ToString().TrimEnd('\r', '\n');
            // PowerShell colours its own output, so the raw text is full of ESC[…m sequences.
            // Emit it as the console-text concept: the registered formatters render the
            // escapes as HTML and strip them from text/plain — this language renders nothing.
            return MimeBundler.Bundle(new DisplayConsoleText(console));
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

    /// <summary>Wraps text in the ANSI codes for the given console colours.</summary>
    private static string Colourize(string text, ConsoleColor? foreground, ConsoleColor? background) {
        if (string.IsNullOrEmpty(text) || (foreground == null && background == null)) {
            return text;
        }
        var codes = new StringBuilder();
        if (foreground != null) {
            codes.Append(AnsiCode(foreground.Value, background: false));
        }
        if (background != null) {
            if (codes.Length > 0) {
                codes.Append(';');
            }
            codes.Append(AnsiCode(background.Value, background: true));
        }
        return "\u001b[" + codes + "m" + text + "\u001b[0m";
    }

    // ConsoleColor's order is not the ANSI order, so this is a lookup rather than arithmetic.
    private static int AnsiCode(ConsoleColor colour, bool background) {
        var code = colour switch {
            ConsoleColor.Black => 30,
            ConsoleColor.DarkBlue => 34,
            ConsoleColor.DarkGreen => 32,
            ConsoleColor.DarkCyan => 36,
            ConsoleColor.DarkRed => 31,
            ConsoleColor.DarkMagenta => 35,
            ConsoleColor.DarkYellow => 33,
            ConsoleColor.Gray => 37,
            ConsoleColor.DarkGray => 90,
            ConsoleColor.Blue => 94,
            ConsoleColor.Green => 92,
            ConsoleColor.Cyan => 96,
            ConsoleColor.Red => 91,
            ConsoleColor.Magenta => 95,
            ConsoleColor.Yellow => 93,
            ConsoleColor.White => 97,
            _ => 39,
        };
        return background ? code + 10 : code;
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
            foreach (var remote in _remoteRunspaces.Values) {
                remote.Dispose();
            }
            _remoteRunspaces.Clear();
        }
    }
}

/// <summary>Thrown for a terminating error in a PowerShell cell.</summary>
public sealed class PowerShellCellException : Exception {
    public PowerShellCellException(string message, Exception inner = null) : base(message, inner) { }
}
