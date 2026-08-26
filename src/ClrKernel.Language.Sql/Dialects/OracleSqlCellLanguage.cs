using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// <c>#!oraclesql</c> — Oracle SQL, on any connection whose provider can carry it.
/// <para>
/// No connect verb of its own. An Oracle connection is declared in
/// <c>connections.json</c> as <c>"$type": "Oracle"</c> (or Odbc/Jdbc), which is
/// where the Jobs connection UI writes it and where a shared one belongs;
/// <c>#!sql-connect</c>'s flags describe a SQL Server login and nothing else, so
/// there is no honest way to spell an Oracle connection inline. The session is
/// shared, so a name declared anywhere resolves here.
/// </para>
/// </summary>
public sealed class OracleSqlCellLanguage : SqlDialectLanguage {
    public OracleSqlCellLanguage(SqlSession session) : base(session) {
    }

    public override string Id => "oraclesql";

    public override string DisplayName => "Oracle SQL";

    public override SqlVocabulary Vocabulary => SqlVocabulary.OracleSql;

    /// <summary>ODP.NET first — it is the one that knows Oracle types. ODBC and JDBC
    /// carry the same statements over a driver somebody else installed.</summary>
    public override IReadOnlyList<string> SupportedProviders { get; } =
        new[] { "Oracle", "Odbc", "Jdbc" };

    public override IReadOnlyList<string> LanguageTags { get; } =
        new[] { "oraclesql", "plsql" };

    public override IReadOnlyList<DirectiveDefinition> Directives { get; } =
        new[] { QueryDirective("#!oraclesql", "Oracle SQL") };
}
