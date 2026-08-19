using System;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Shell;

/// <summary>Parses <c>#!shell-connect</c> and the per-cell <c>--connection</c> flag.</summary>
public static class ShellDirectives {
    /// <summary>The declarative shape of <c>#!shell-connect</c>.</summary>
    public static readonly DirectiveDefinition ConnectDefinition = new() {
        Selector = "#!shell-connect",
        Description = "Registers a named SSH target for shell cells.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true, Description = "Connection name." },
            new() { Name = "--host", Aliases = new[] { "--server", "-h" }, Required = true, Description = "Remote host." },
            new() { Name = "--user", Aliases = new[] { "--username", "-u" }, Description = "User name." },
            new() { Name = "--port", Aliases = new[] { "-p" }, Description = "SSH port (default 22)." },
            new() { Name = "--identity", Aliases = new[] { "-i" }, Description = "SSH identity file." },
            new() { Name = "--remote-shell", Aliases = new[] { "--shell" }, Description = "Shell binary on the remote (bash, zsh, sh)." },
            new() { Name = "--password", Kind = DirectiveParameterKind.Forbidden,
                ForbiddenMessage = "SSH targets use key authentication (your keys, agent, and ~/.ssh/config apply); " +
                    "passwords are not supported and must never be placed in a notebook." },
        },
    };

    /// <summary>
    /// Parses a <c>#!shell-connect</c> line. Flags: <c>--name</c>, <c>--host</c>,
    /// <c>--user</c>, <c>--port</c>, <c>--identity</c>. A <c>--password</c> is rejected
    /// on purpose — SSH auth is key-based (agent and ~/.ssh/config apply).
    /// </summary>
    public static ShellConnectionSpec ParseConnect(string line) {
        var args = DirectiveParser.Parse(ConnectDefinition, line);
        var spec = new ShellConnectionSpec {
            Name = args.Get("--name"),
            Host = args.Get("--host"),
            User = args.Get("--user"),
            IdentityFile = args.Get("--identity"),
        };
        if (args.Has("--port")) {
            spec.Port = int.TryParse(args.Get("--port"), out var port)
                ? port
                : throw new FormatException("--port expects a number.");
        }
        if (args.Has("--remote-shell")) {
            spec.RemoteShell = args.Get("--remote-shell").ToLowerInvariant();
        }
        return spec;
    }

    /// <summary>The <c>--connection &lt;name&gt;</c> from a selector line, or null (local).</summary>
    public static string SelectorConnection(string firstLine) =>
        DirectiveParser.FindValue(firstLine, "--connection");
}
