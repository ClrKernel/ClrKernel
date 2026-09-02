using System;
using System.Text;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// The one name every provider's naming derives from. A library consumer that
/// embeds ClrKernel under its own brand passes its prefix to
/// <see cref="SecretStore"/> (or to a provider's constructor) and gets
/// <c>ACME_SECRET_*</c> variables and an "Acme" keychain service instead of the
/// ClrKernel defaults, without a fork. The stores spell names differently — a
/// service name verbatim, an environment variable upper-cased — so both forms
/// are derived here rather than asked for separately.
/// </summary>
public static class SecretPrefix {
    /// <summary>Used when a consumer specifies nothing. Every default name in
    /// this assembly comes from it.</summary>
    public const string Default = "ClrKernel";

    /// <summary>
    /// The prefix to actually use: the caller's, or <see cref="Default"/> when
    /// they passed nothing. Null and blank both mean "unspecified" — a blank
    /// prefix would produce variable names starting with '_'.
    /// </summary>
    public static string OrDefault(string prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? Default : prefix.Trim();

    /// <summary>
    /// The environment-variable form of a prefix: upper-cased, with every
    /// non-alphanumeric character folded to '_' — the same mapping keys get, so
    /// "ClrKernel" gives <c>CLRKERNEL</c> and "acme corp" gives <c>ACME_CORP</c>.
    /// </summary>
    public static string ToEnvironmentToken(string prefix) =>
        ToEnvironmentName(OrDefault(prefix));

    /// <summary>
    /// Upper-cases <paramref name="text"/> and folds non-alphanumerics to '_'.
    /// Shared by the prefix and the key so a name is spelled one way only.
    /// </summary>
    internal static string ToEnvironmentName(string text) {
        if (text == null) {
            throw new ArgumentNullException(nameof(text));
        }
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        return sb.ToString();
    }
}
