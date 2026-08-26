using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// <c>#!ansisql</c> — standard SQL, for a database this kernel has no dialect for.
/// <para>
/// It completes only words that are in the standard, which is the point: against
/// a PostgreSQL or MySQL connection over ODBC, offering T-SQL's <c>NVARCHAR</c>
/// would be the editor asserting something false. When a real dialect for that
/// database arrives it is a new registration and this stays where it is.
/// </para>
/// </summary>
public sealed class AnsiSqlCellLanguage : SqlDialectLanguage {
    public AnsiSqlCellLanguage(SqlSession session) : base(session) {
    }

    public override string Id => "ansisql";

    public override string DisplayName => "SQL (Generic)";

    public override SqlVocabulary Vocabulary => SqlVocabulary.AnsiSql;

    public override string Monogram => "SQL";

    /// <summary>The two providers that reach a database this kernel has no
    /// first-party client for.</summary>
    public override IReadOnlyList<string> SupportedProviders { get; } =
        new[] { "Odbc", "Jdbc" };

    public override IReadOnlyList<string> LanguageTags { get; } =
        new[] { "ansisql" };

    public override IReadOnlyList<DirectiveDefinition> Directives { get; } =
        new[] { QueryDirective("#!ansisql", "standard SQL") };
}
