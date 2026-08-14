using System;
using System.Collections.Generic;
using ClrKernel.Database;

namespace ClrKernel.Language.Shell;

/// <summary>
/// A named SSH target for shell cells. Authentication is the system <c>ssh</c>
/// client's business — keys, agent, and <c>~/.ssh/config</c> all apply, and
/// <c>BatchMode=yes</c> makes a missing key fail fast instead of hanging on a
/// password prompt. Passwords are deliberately unsupported (no sshpass): keys are
/// the norm and nothing secret ever reaches a notebook or config file.
/// </summary>
public sealed class ShellConnectionSpec {
    public string Name { get; set; }
    public string Host { get; set; }
    public string User { get; set; }
    public int Port { get; set; }
    public string IdentityFile { get; set; }

    public string Describe() =>
        (string.IsNullOrEmpty(User) ? "" : User + "@") + Host + (Port > 0 && Port != 22 ? ":" + Port : "");

    /// <summary>The ssh argument list for running <paramref name="remoteCommand"/> on this target.</summary>
    public IReadOnlyList<string> BuildSshArguments(string remoteCommand) {
        if (string.IsNullOrWhiteSpace(Host)) {
            throw new ShellCellException($"SSH connection '{Name}' has no host.");
        }
        // BatchMode: never hang on a password prompt. accept-new: BatchMode also
        // can't answer the first-contact "accept host key?" question, which made
        // every never-seen host fail with "Host key verification failed" — accept
        // unknown hosts, still hard-fail if a known host's key CHANGES.
        var args = new List<string> { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new" };
        if (Port > 0 && Port != 22) {
            args.Add("-p");
            args.Add(Port.ToString());
        }
        if (!string.IsNullOrEmpty(IdentityFile)) {
            args.Add("-i");
            args.Add(IdentityFile);
        }
        args.Add(string.IsNullOrEmpty(User) ? Host : User + "@" + Host);
        args.Add(remoteCommand);
        return args;
    }
}

/// <summary>Maps a <see cref="ShellConnectionSpec"/> from a <c>connections.json</c>
/// <c>"$type": "Ssh"</c> node (host/user/port/identity — nothing secret to hold).</summary>
public static class ShellConnectionConfig {
    /// <summary>The <c>$type</c> discriminator for SSH target nodes.</summary>
    public const string TypeName = "Ssh";

    public static ShellConnectionSpec FromNode(RawConnectionNode node) {
        if (node == null) {
            throw new ArgumentNullException(nameof(node));
        }
        return new ShellConnectionSpec {
            Name = node.Name,
            Host = node.Get("host") ?? node.Get("server"),
            User = node.Get("user") ?? node.Get("username"),
            Port = int.TryParse(node.Get("port"), out var port) ? port : 0,
            IdentityFile = node.Get("identity") ?? node.Get("identityFile"),
        };
    }
}
