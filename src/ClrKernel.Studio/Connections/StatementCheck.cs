using System;
using System.Collections.Generic;
using System.Text;

namespace ClrKernel.Studio;

/// <summary>
/// A quick look at what a statement starts with, so somebody running as a
/// read-only login gets told why rather than a permissions error from the server.
/// <para>
/// <b>This is not the mechanism and must never be treated as one.</b> The boundary
/// is the least-privilege credential the connection carries; that is enforced by
/// the database, which is the only thing that can enforce it. Statement inspection
/// loses to <c>EXEC sp_whatever</c>, to a CTE that ends in an <c>INSERT</c>, to
/// <c>SELECT … INTO</c> spelled across lines, and to anything built as a string and
/// executed. Every one of those reaches the server, and the server refuses it —
/// which is the design, not a gap in this file.
/// </para>
/// <para>
/// So this is deliberately literal: it reports a verb only when a statement plainly
/// begins with one. Anything clever here would trade a better message for a worse
/// failure mode — refusing a legitimate <c>SELECT</c> because it mentioned the word
/// delete is a bug people cannot work around, while missing a write is a message
/// they never see before the database says no anyway.
/// </para>
/// </summary>
public static class StatementCheck {
    /// <summary>Verbs that plainly are not reads. Deliberately short: every entry
    /// has to be something no <c>SELECT</c> ever starts with.</summary>
    private static readonly HashSet<string> _writes = new(StringComparer.OrdinalIgnoreCase) {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
        "CREATE", "ALTER", "DROP",
        "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE",
        "BACKUP", "RESTORE",
    };

    /// <summary>
    /// The first plainly-not-a-read verb in <paramref name="sql"/>, or null when
    /// nothing in it starts with one.
    /// </summary>
    public static string WriteVerbIn(string sql) {
        foreach (var statement in Statements(Strip(sql))) {
            var word = FirstWord(statement);
            if (word != null && _writes.Contains(word)) {
                return word.ToUpperInvariant();
            }
        }
        return null;
    }

    /// <summary>The message a person sees. It says where the boundary actually is,
    /// because somebody who has just been refused is exactly the person who needs to
    /// know that the app is not the thing enforcing it.</summary>
    public static string Refusal(string verb) =>
        $"This connection runs you as its read-only login, and {verb} is not a read. "
        + "The database is what enforces that — this is only telling you sooner.";

    /// <summary>
    /// Comments and quoted text removed, so a word inside them cannot be read as a
    /// verb. <c>SELECT 'delete me' AS note</c> is a read, and a scanner that could
    /// not tell would be worse than no scanner.
    /// </summary>
    private static string Strip(string sql) {
        var text = sql ?? string.Empty;
        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') {
                while (i < text.Length && text[i] != '\n') {
                    i++;
                }
                kept.Append('\n');
            } else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                kept.Append(' ');
            } else if (c == '\'' || c == '"' || c == '[') {
                // A quoted string or a quoted identifier. Both are values as far as
                // this is concerned: whatever is inside is not a verb.
                var close = c == '[' ? ']' : c;
                i++;
                while (i < text.Length && text[i] != close) {
                    i++;
                }
                kept.Append(' ');
            } else {
                kept.Append(c);
            }
        }
        return kept.ToString();
    }

    /// <summary>Split on the separators a batch actually uses: <c>;</c> between
    /// statements, and <c>GO</c> alone on a line between batches.</summary>
    private static IEnumerable<string> Statements(string sql) {
        foreach (var batch in sql.Split(';')) {
            var start = 0;
            var lines = batch.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                if (lines[i].Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)) {
                    yield return string.Join("\n", lines, start, i - start);
                    start = i + 1;
                }
            }
            yield return string.Join("\n", lines, start, lines.Length - start);
        }
    }

    /// <summary>
    /// The first word, skipping the ones that can precede a verb without changing
    /// what the statement is. <c>WITH</c> is not among them, deliberately: a CTE can
    /// end in an <c>INSERT</c>, and reading past it to guess would be exactly the
    /// cleverness that gets this wrong in the other direction.
    /// </summary>
    private static string FirstWord(string statement) {
        foreach (var token in statement.Split(
            new[] { ' ', '\t', '\r', '\n', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)) {
            var word = token.Trim();
            if (word.Length == 0) {
                continue;
            }
            return word;
        }
        return null;
    }
}
