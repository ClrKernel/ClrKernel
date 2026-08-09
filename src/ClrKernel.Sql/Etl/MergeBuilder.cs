using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClrKernel.Sql.Etl;
/// <summary>Describes an upsert: match a target to a source on key columns.</summary>
public sealed class MergeSpec {
    public string Target { get; set; }

    /// <summary>A table/view name, or a SELECT query (auto-detected, or force with
    /// <see cref="SourceIsQuery"/>).</summary>
    public string Source { get; set; }

    public bool? SourceIsQuery { get; set; }

    /// <summary>Key columns used to match rows (required).</summary>
    public IList<string> KeyColumns { get; set; } = new List<string>();

    /// <summary>Non-key columns updated on a match. When null, the runner fills
    /// this by introspecting the target schema.</summary>
    public IList<string> UpdateColumns { get; set; }

    /// <summary>Columns inserted for new rows. Defaults to keys + update columns.</summary>
    public IList<string> InsertColumns { get; set; }

    /// <summary>Also delete target rows missing from the source.</summary>
    public bool DeleteNotMatchedBySource { get; set; }

    /// <summary>Emit a trailing SELECT of Inserted/Updated/Deleted counts.</summary>
    public bool WithOutputCounts { get; set; } = true;
}

/// <summary>Per-action row counts from a MERGE.</summary>
public sealed class MergeResult {
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Deleted { get; set; }
    public long ElapsedMs { get; set; }
    public override string ToString() =>
        $"inserted {Inserted:N0}, updated {Updated:N0}, deleted {Deleted:N0} ({ElapsedMs:N0} ms)";
}

/// <summary>
/// Generates a T-SQL <c>MERGE</c> from a <see cref="MergeSpec"/>. Identifiers are
/// quoted (injection-safe) and the output is valid T-SQL (verified with ScriptDom
/// in tests). Kept pure/side-effect-free so it can be unit-tested without a server.
/// </summary>
public static class MergeBuilder {
    public static string Build(MergeSpec spec) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        if (string.IsNullOrWhiteSpace(spec.Target)) {
            throw new ArgumentException("MERGE requires a target.", nameof(spec));
        }
        if (string.IsNullOrWhiteSpace(spec.Source)) {
            throw new ArgumentException("MERGE requires a source.", nameof(spec));
        }
        var keys = (spec.KeyColumns ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Count == 0) {
            throw new ArgumentException("MERGE requires at least one key column (--on).", nameof(spec));
        }
        if (spec.UpdateColumns == null) {
            throw new InvalidOperationException(
                "Update columns are not set. Provide them, or run through MergeRunner which " +
                "introspects the target schema.");
        }

        var updates = spec.UpdateColumns.Where(c => !string.IsNullOrWhiteSpace(c) && !keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var inserts = (spec.InsertColumns != null && spec.InsertColumns.Count > 0)
            ? spec.InsertColumns.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
            : keys.Concat(updates).ToList();

        var target = SqlIdentifier.Quote(spec.Target);
        var source = ResolveSource(spec);

        var sb = new StringBuilder();
        if (spec.WithOutputCounts) {
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine("DECLARE @clr_actions TABLE (act NVARCHAR(10));");
        }
        sb.AppendLine($"MERGE {target} AS T");
        sb.AppendLine($"USING {source} AS S");
        sb.AppendLine("ON (" + string.Join(" AND ", keys.Select(k => $"T.{SqlIdentifier.QuotePart(k)} = S.{SqlIdentifier.QuotePart(k)}")) + ")");

        if (updates.Count > 0) {
            sb.AppendLine("WHEN MATCHED THEN UPDATE SET " +
                string.Join(", ", updates.Select(c => $"T.{SqlIdentifier.QuotePart(c)} = S.{SqlIdentifier.QuotePart(c)}")));
        }

        var insertCols = string.Join(", ", inserts.Select(SqlIdentifier.QuotePart));
        var insertVals = string.Join(", ", inserts.Select(c => $"S.{SqlIdentifier.QuotePart(c)}"));
        sb.AppendLine($"WHEN NOT MATCHED BY TARGET THEN INSERT ({insertCols}) VALUES ({insertVals})");

        if (spec.DeleteNotMatchedBySource) {
            sb.AppendLine("WHEN NOT MATCHED BY SOURCE THEN DELETE");
        }

        if (spec.WithOutputCounts) {
            sb.AppendLine("OUTPUT $action INTO @clr_actions;");
            sb.AppendLine("SELECT");
            sb.AppendLine("  SUM(CASE WHEN act = 'INSERT' THEN 1 ELSE 0 END) AS Inserted,");
            sb.AppendLine("  SUM(CASE WHEN act = 'UPDATE' THEN 1 ELSE 0 END) AS Updated,");
            sb.AppendLine("  SUM(CASE WHEN act = 'DELETE' THEN 1 ELSE 0 END) AS Deleted");
            sb.Append("FROM @clr_actions;");
        } else {
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static string ResolveSource(MergeSpec spec) {
        var src = spec.Source.Trim();
        var isQuery = spec.SourceIsQuery
            ?? (src.StartsWith("(") || src.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        if (isQuery) {
            var inner = src.TrimEnd(';');
            return inner.StartsWith("(") ? inner : "(" + inner + ")";
        }
        return SqlIdentifier.Quote(src);
    }
}
