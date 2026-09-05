using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// Secrets a notebook resolves by name, kept per branch.
///
/// <para>
/// <c>OPENAI</c> on <c>test</c> and <c>OPENAI</c> on <c>prod</c> are two secrets
/// with two values, and a notebook only ever sees its own branch's. That is
/// arranged where the kernel is started, not inside the kernel: Studio resolves the
/// branch's secrets and hands them to the child process as
/// <c>CLRKERNEL_SECRET_&lt;NAME&gt;</c>, which the kernel's own environment
/// provider already answers from. Nothing in <c>Core.Secrets</c> had to learn about
/// branches, and the scoping is strict by construction — the kernel is never given
/// another branch's values, so it cannot fall back to them.
/// </para>
/// <para>
/// The value itself is stored under a key naming the project and branch, so two
/// branches cannot collide in the one flat namespace every credential store has.
/// </para>
/// </summary>
public sealed class BranchSecrets {
    private readonly Func<RunsDbContext> _contextFactory;
    private readonly SecretStore _secrets;
    private readonly ILogger _logger;

    public BranchSecrets(Func<RunsDbContext> contextFactory, SecretStore secrets, ILogger logger = null) {
        _contextFactory = contextFactory;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Whether a value written now would survive a restart.</summary>
    public bool CanPersist => _secrets.CanPersist;

    /// <summary>
    /// The key a value is stored under. Namespaced by project and branch because
    /// every store underneath is one flat namespace — a keychain service, one JSON
    /// file, one set of environment variables — and `OPENAI` on two branches has to
    /// be two entries.
    /// </summary>
    public static string KeyFor(string project, string branch, string name) =>
        $"clrkernel-studio:secret:{project}:{branch}:{name}";

    /// <summary>Why this is not a usable secret name, or null.</summary>
    /// <remarks>
    /// It becomes an environment variable in the kernel, so it has to survive
    /// <c>EnvName</c>'s folding intact: that maps every non-alphanumeric to an
    /// underscore, so <c>my-key</c> and <c>my_key</c> would arrive as one variable
    /// and quietly overwrite each other.
    /// </remarks>
    public static string Problem(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return "A secret needs a name.";
        }
        if (name.Length > 64) {
            return "A secret name can be at most 64 characters.";
        }
        if (!char.IsAsciiLetter(name[0])) {
            return "A secret name starts with a letter.";
        }
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? null
            : "A secret name can hold letters, digits and underscores — it becomes an "
                + "environment variable in the kernel, and anything else folds into one.";
    }

    /// <summary>The names on a branch, and whether each currently resolves.</summary>
    public async Task<IReadOnlyList<(SecretName Row, bool IsSet)>> ListAsync(string project, string branch) {
        await using var db = _contextFactory();
        var rows = await db.SecretNames
            .Where(s => s.Project == project && s.Branch == branch)
            .OrderBy(s => s.Name)
            .ToListAsync();
        return rows.Select(r => (r, _secrets.TryResolve(KeyFor(project, branch, r.Name), out _))).ToList();
    }

    /// <summary>
    /// Stores a value and remembers the name. Returns the refusal, or null.
    /// </summary>
    public async Task<string> SetAsync(
        string project, string branch, string name, string value, Guid? by, string byName) {
        if (Problem(name) is { } problem) {
            return problem;
        }
        if (string.IsNullOrEmpty(value)) {
            return "A secret needs a value.";
        }
        if (!_secrets.CanPersist) {
            return "This server has nowhere to keep a secret, so one cannot be saved here. "
                + $"Set the {_secrets.EnvName(KeyFor(project, branch, name))} environment variable "
                + "instead, or give this server a credential store or a secrets file.";
        }
        _secrets.Store(KeyFor(project, branch, name), value);

        await using var db = _contextFactory();
        var row = await db.SecretNames.FirstOrDefaultAsync(
            s => s.Project == project && s.Branch == branch && s.Name == name);
        if (row == null) {
            db.SecretNames.Add(new SecretName {
                Id = Guid.NewGuid(),
                Project = project,
                Branch = branch,
                Name = name,
                CreatedBy = by,
                CreatedByName = byName,
                CreatedAt = DateTime.UtcNow,
            });
        } else {
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        _logger?.LogInformation(
            "Secret {Name} set on {Project}/{Branch} by {Who}.", name, project, branch, byName);
        return null;
    }

    /// <summary>Forgets a secret: the value and the name both.</summary>
    public async Task<bool> DeleteAsync(string project, string branch, string name, string byName) {
        await using var db = _contextFactory();
        var removed = await db.SecretNames
            .Where(s => s.Project == project && s.Branch == branch && s.Name == name)
            .ExecuteDeleteAsync();
        try {
            _secrets.Delete(KeyFor(project, branch, name));
        } catch (Exception e) {
            // The name is gone either way; a store that will not delete is worth a
            // line in the log rather than a failed request.
            _logger?.LogWarning("Could not remove the value for {Name}: {Error}", name, e.Message);
        }
        if (removed > 0) {
            _logger?.LogInformation(
                "Secret {Name} removed from {Project}/{Branch} by {Who}.", name, project, branch, byName);
        }
        return removed > 0;
    }

    /// <summary>
    /// The environment a kernel started on this branch should carry: every secret
    /// named here that currently resolves, under the variable the kernel's own
    /// secret store reads.
    ///
    /// <para>
    /// A name with no value is left out rather than passed as empty — a cell
    /// resolving it then fails saying so, which is the honest answer, instead of
    /// authenticating with nothing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EnvironmentForAsync(
        string project, string branch) {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (row, isSet) in await ListAsync(project, branch)) {
            if (isSet && _secrets.TryResolve(KeyFor(project, branch, row.Name), out var value)) {
                environment[EnvironmentSecretProvider.EnvName(row.Name, null)] = value;
            }
        }
        return environment;
    }
}
