using System.Text.RegularExpressions;

namespace ClrKernel.Sql.Deploy;
/// <summary>
/// Makes a definition batch idempotent by rewriting a leading
/// <c>CREATE PROCEDURE|VIEW|FUNCTION|TRIGGER</c> to <c>CREATE OR ALTER …</c>, so
/// deploying the same folder repeatedly just updates the object in place.
/// Batches that are already <c>CREATE OR ALTER</c>, or that create other object
/// kinds (e.g. tables), are left untouched — guard those with your own
/// <c>IF NOT EXISTS</c>.
/// </summary>
public static class CreateOrAlter {
    private static readonly Regex _create = new Regex(
        @"\bCREATE\s+(PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Rewrites the first eligible CREATE in the batch; returns the batch
    /// unchanged when nothing matches.</summary>
    public static string Transform(string batch) {
        if (string.IsNullOrEmpty(batch)) {
            return batch;
        }
        return _create.Replace(batch, m => "CREATE OR ALTER " + m.Groups[1].Value, 1);
    }
}
