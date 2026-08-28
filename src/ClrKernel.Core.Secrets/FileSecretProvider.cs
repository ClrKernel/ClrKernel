using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// A JSON file of secrets, for machines with no OS credential store — a
/// container being the case that forced it. Without this, a self-hosted server
/// can only take passwords as <c>CLRKERNEL_SECRET_*</c> variables, which means
/// editing the deployment and restarting every time somebody adds a connection.
/// <para>
/// Opt-in: it exists only when <see cref="PathVariable"/> names a file. That
/// keeps it out of the way on a laptop, where the Keychain or Credential Manager
/// is both available and better.
/// </para>
/// <para>
/// <b>The values are stored in plain text</b>, in a file with no group or world
/// permissions. That is honest rather than lazy: encrypting them with a key kept
/// beside them is the same threat model with more moving parts. Treat the file
/// like a private key — it is exactly as protected as the volume it sits on, and
/// it must never be inside a git worktree.
/// </para>
/// </summary>
public sealed class FileSecretProvider : ISecretProvider {
    /// <summary>
    /// Deliberately not <c>CLRKERNEL_SECRET_FILE</c>: that is the variable a
    /// secret named "file" would resolve from, and the two would collide.
    /// </summary>
    public const string PathVariable = "CLRKERNEL_SECRETS_FILE";

    private readonly string _path;
    private readonly object _gate = new object();

    public FileSecretProvider(string path) {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The configured provider, or null when the variable is unset.</summary>
    public static FileSecretProvider TryCreate() =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path
            ? new FileSecretProvider(path)
            : null;

    public string Name => "file";
    public bool CanStore => true;

    public bool TryGet(string key, out string secret) {
        // Read through rather than caching: the kernel runs as a separate process
        // and would otherwise keep serving a password the web app has replaced.
        lock (_gate) {
            secret = Read().TryGetValue(key, out var value) ? value : null;
        }
        return secret != null;
    }

    public void Set(string key, string secret) {
        lock (_gate) {
            var all = Read();
            all[key] = secret;
            Write(all);
        }
    }

    public void Delete(string key) {
        lock (_gate) {
            var all = Read();
            if (all.Remove(key)) {
                Write(all);
            }
        }
    }

    private Dictionary<string, string> Read() {
        try {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            // A missing file is the first run; a corrupt one must not take the
            // server down on a read. Set() below will rewrite it either way —
            // which does lose a hand-mangled file, so it is not silent.
            if (e is JsonException) {
                Console.Error.WriteLine($"{_path}: not readable as a secrets file ({e.Message}).");
            }
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, string> all) {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }
        // Written beside and moved into place: a reader in the other process never
        // sees half a file, and the mode is set before the content is visible.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, _json));
        Restrict(temporary);
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>
    /// Owner-only. The container's umask gives 0644 by default, which would leave
    /// every password readable by anything else running in it.
    /// </summary>
    private static void Restrict(string path) {
        if (OperatingSystem.IsWindows()) {
            return;
        }
        try {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"{path}: could not restrict permissions ({e.Message}).");
        }
    }

    private static readonly JsonSerializerOptions _json = new JsonSerializerOptions {
        WriteIndented = true,
    };
}
