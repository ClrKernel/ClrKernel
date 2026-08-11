using Microsoft.Data.SqlClient;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>
/// A lazy SQL query bound to a <see cref="SqlDatabase"/>. Nothing runs until you
/// call <see cref="DataSourceQuery.Results(int)"/> (materialized rows that also render
/// as an interactive grid), <see cref="DataSourceQuery.Results{T}"/> (typed objects), or
/// <see cref="OpenReader"/> (a streaming reader, e.g. to feed a bulk copy).
/// <para>
/// Everything but the reader's type is inherited: SQL Server's only addition here is
/// that <see cref="OpenReader"/> hands back a <see cref="SqlDataReader"/>, which is what
/// <c>SqlBulkCopy</c> consumes.
/// </para>
/// </summary>
public sealed class SqlQuery : DataSourceQuery {
    internal SqlQuery(SqlDatabase database, string sql, object parameters)
        : base(database, sql, parameters) { }

    /// <summary>
    /// Opens a streaming reader on its own connection (closed when the reader is
    /// disposed). Use this to pipe rows into a bulk copy without buffering.
    /// </summary>
    public override SqlDataReader OpenReader() => (SqlDataReader)base.OpenReader();
}
