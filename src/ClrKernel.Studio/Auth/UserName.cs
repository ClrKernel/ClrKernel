using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClrKernel.Studio;

/// <summary>
/// The handle an account is known by everywhere a machine reads it: the git branch
/// <c>user/&lt;name&gt;</c>, the worktree directory <c>user-&lt;name&gt;</c>, and the
/// author address on every commit that person makes.
///
/// <para>
/// It is the third of a user's three names and the only one with rules.
/// <c>Id</c> is immutable and invisible — it is the WebAuthn user handle inside
/// every issued passkey, so it can never change. <c>DisplayName</c> is what screens
/// show: free text, changeable, and not unique. This one sits between them: unique,
/// safe for git and for a filesystem, and changeable only by an admin, because
/// changing it moves a branch and a directory.
/// </para>
/// </summary>
public static class UserName {
    /// <summary>
    /// Long enough for a real name, short enough that
    /// <c>&lt;workspace&gt;/user-&lt;name&gt;</c> stays inside a Windows path.
    /// </summary>
    public const int MaxLength = 39;

    /// <summary>
    /// Names that would collide with a branch this workspace already has.
    /// <c>mine</c> is not a git branch but is the URL alias for "my own", so a
    /// person called <c>mine</c> would be unreachable through the API.
    /// </summary>
    private static readonly string[] _reserved = {
        GitService.TestBranch, GitService.ProdBranch, "dev", ProjectRegistry.MineEnvironment,
    };

    /// <summary>Why this is not a usable handle, or null when it is.</summary>
    /// <remarks>
    /// The charset is what <c>git check-ref-format</c> accepts *and* what is safe as
    /// a directory name and a command-line argument, which is narrower: git allows
    /// <c>-jeremy</c> and <c>jeremy@corp.com</c>, but a leading dash is read as a
    /// flag by every tool that later takes this as an argument.
    /// </remarks>
    public static string Problem(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return "A username is required.";
        }
        if (name.Length > MaxLength) {
            return $"A username can be at most {MaxLength} characters.";
        }
        if (name != name.ToLowerInvariant()) {
            // Lower-cased rather than rejected would be kinder, but the caller has
            // to know: two people typing Jeremy and jeremy are one directory on
            // macOS and Windows, and silently folding hides that from whoever typed
            // the second one.
            return "A username is lower-case — the branch and the folder it names are.";
        }
        if (!char.IsAsciiLetterOrDigit(name[0])) {
            return "A username starts with a letter or a digit.";
        }
        if (name.EndsWith('.')) {
            return "A username cannot end with a dot — git refuses the branch name.";
        }
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) {
            return "A username can hold letters, digits, dot, dash and underscore.";
        }
        if (_reserved.Contains(name, StringComparer.Ordinal)) {
            return $"'{name}' is the name of a branch, so it cannot be a username.";
        }
        return null;
    }

    public static bool IsValid(string name) => Problem(name) == null;

    /// <summary>
    /// A starting suggestion from a display name — offered to whoever is creating
    /// the account, never applied without them seeing it. Not the validator's
    /// inverse: it can return something already taken, which is the caller's to
    /// resolve.
    /// </summary>
    public static string Suggest(string displayName) {
        var builder = new StringBuilder();
        // Decomposed first, so an accent becomes a letter plus a mark and the mark
        // is what gets dropped: José suggests `jose`. Project.SlugFor does not do
        // this and does not need to — it slugs a project's name, and this slugs a
        // person's, where "jos-garc-a" is a poor thing to offer somebody.
        var text = (displayName ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        foreach (var c in text) {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) {
                continue;
            }
            if (char.IsAsciiLetterOrDigit(c)) {
                builder.Append(char.ToLowerInvariant(c));
            } else if (builder.Length > 0 && builder[^1] != '-') {
                builder.Append('-');
            }
        }
        var slug = builder.ToString().Trim('-', '.');
        if (slug.Length > MaxLength) {
            slug = slug[..MaxLength].TrimEnd('-', '.');
        }
        // Everything unusable — punctuation only, a non-latin script, or a name
        // that happens to be a branch — falls back to one that is always valid.
        // `user` itself is not reserved: `user/user` is a perfectly good branch.
        return IsValid(slug) ? slug : "user";
    }

    /// <summary>
    /// <paramref name="wanted"/> if nothing has it, else the same with a number on
    /// the end. Used when backfilling handles for accounts that never had one.
    /// </summary>
    public static string Unique(string wanted, IEnumerable<string> taken) {
        var used = new HashSet<string>(taken ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(wanted)) {
            return wanted;
        }
        // Truncate before appending: the suffix must not push it over the limit.
        for (var n = 2; ; n++) {
            var suffix = n.ToString(CultureInfo.InvariantCulture);
            var stem = wanted.Length + suffix.Length > MaxLength
                ? wanted[..(MaxLength - suffix.Length)].TrimEnd('-', '.')
                : wanted;
            var candidate = stem + suffix;
            if (!used.Contains(candidate)) {
                return candidate;
            }
        }
    }
}
