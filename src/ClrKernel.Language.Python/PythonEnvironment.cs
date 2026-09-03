using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Language.Python;

/// <summary>
/// The packages a notebook's cells can import.
///
/// <para>
/// One directory per notebook directory, installed into with <c>uv pip install
/// --target</c> and put on <c>sys.path</c> by the driver. Deliberately **not** a
/// venv: a venv would have to become the interpreter, so installing anything
/// mid-session would mean restarting the interpreter and losing every name the
/// notebook had bound. A target directory is the same isolation without that —
/// install in one cell, import in the next, nothing else disturbed.
/// </para>
/// <para>
/// It lives in the kernel's cache rather than beside the notebook, which settles
/// the question the spec raised: there is no <c>.venv/</c> for git to ignore and
/// nothing for Studio to promote between branches. What belongs in the repo is
/// <c>requirements.txt</c> — the definition — not the installed bytes, the same
/// split as a lock file against the NuGet cache.
/// </para>
/// </summary>
public static class PythonEnvironment {
    /// <summary>Read when <c>#!python-install</c> is given no packages of its own.</summary>
    public const string RequirementsFile = "requirements.txt";

    /// <summary>
    /// Where this notebook's packages live. Per *directory*, because a working
    /// directory is the only notebook identity a cell language is given — and a
    /// folder of notebooks sharing one set of packages is what a Python project
    /// looks like anyway.
    /// </summary>
    public static string PackagesFor(string workingDirectory) {
        var root = Path.Combine(PythonInterpreter.Home(), "packages");
        if (string.IsNullOrWhiteSpace(workingDirectory)) {
            // An unsaved buffer with nowhere to belong; shared rather than a new
            // directory per process, which would leak one per session.
            return Path.Combine(root, "scratch");
        }
        var full = Path.GetFullPath(workingDirectory);
        // Named for legibility, hashed for uniqueness: two `notebooks` directories in
        // different repos must not share packages, and the hash is what guarantees it.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        var name = new string(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(root, $"{(name.Length > 0 ? name : "notebook")}-{hash}");
    }

    /// <summary>The packages named in <c>requirements.txt</c> beside the notebook, or
    /// null when there is no such file.</summary>
    public static IReadOnlyList<string> Requirements(string workingDirectory) {
        if (string.IsNullOrWhiteSpace(workingDirectory)) {
            return null;
        }
        var path = Path.Combine(workingDirectory, RequirementsFile);
        return File.Exists(path) ? new[] { "-r", path } : null;
    }

    /// <summary>
    /// Installs into this notebook's packages directory. Returns whatever uv said,
    /// which is worth showing: it names every version it resolved.
    /// </summary>
    public static async Task<string> InstallAsync(
        string interpreter, string packages, IReadOnlyList<string> arguments,
        Action<string> log = null, CancellationToken cancellationToken = default) {
        var uv = await PythonProvisioner.EnsureUvAsync(log, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(packages);

        // --python so uv resolves wheels for the interpreter that will import them,
        // not for whatever it would pick on its own: a wheel with a compiled
        // extension built for the wrong ABI imports and then crashes.
        // ponytail: no --offline, so a machine with a warm uv cache and no network
        // still fails here even though every wheel it needs is on disk. Pass it
        // through when someone air-gapped actually wants this directive.
        var command = new List<string> { "pip", "install", "--python", interpreter, "--target", packages };
        command.AddRange(arguments);
        return await PythonProvisioner.RunAsync(uv, command.ToArray(), log, cancellationToken)
            .ConfigureAwait(false);
    }
}
