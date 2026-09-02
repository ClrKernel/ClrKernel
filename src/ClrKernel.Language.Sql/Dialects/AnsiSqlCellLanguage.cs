using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// <c>#!ansisql</c> — standard SQL, for a database this kernel has no dialect for.
/// <para>
/// It completes only words that are in the standard, which is the point: against
/// a PostgreSQL or MySQL connection, offering T-SQL's <c>NVARCHAR</c> would be the
/// editor asserting something false. When a real dialect for that database arrives
/// it is a new registration and this stays where it is.
/// </para>
/// </summary>
public sealed class AnsiSqlCellLanguage : SqlDialectLanguage {
    public AnsiSqlCellLanguage(SqlSession session) : base(session) {
    }

    public override string Id => "ansisql";

    public override string DisplayName => "SQL (Generic)";

    public override SqlVocabulary Vocabulary => SqlVocabulary.AnsiSql;

    public override string Monogram => "SQL";

    /// <summary>
    /// ODBC and JDBC reach a database this kernel has no client for; Postgres is a
    /// first-party provider with no dialect of its own.
    /// <para>
    /// Postgres is here because a compatibility declaration is what this is, not
    /// part of a language's identity — a PostgreSQL connection was reachable from
    /// C# through <c>DataSource</c> and from the query editor, and refused by every
    /// SQL cell in the kernel, which is a hole rather than a decision. Standard SQL
    /// is what a generic dialect should offer it. A `pgsql` dialect that knows
    /// Postgres' own vocabulary is still the better answer and is still a new
    /// registration, exactly as the note above says.
    /// </para>
    /// </summary>
    public override IReadOnlyList<string> SupportedProviders { get; } =
        new[] { "Postgres", "Odbc", "Jdbc" };

    public override IReadOnlyList<string> LanguageTags { get; } =
        new[] { "ansisql" };

    public override IReadOnlyList<DirectiveDefinition> Directives { get; } =
        new[] { QueryDirective("#!ansisql", "standard SQL") };
}
