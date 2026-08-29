using System.Collections.Generic;

namespace ClrKernel.Language.Sql;

/// <summary>
/// The words one SQL dialect knows: what completion offers and what hover
/// explains.
/// <para>
/// Per dialect rather than one SQL-shaped pile, because the pile is what this
/// splits up: offering <c>NVARCHAR</c> in an Oracle cell and <c>NVL</c> in a
/// T-SQL one is worse than offering nothing — it reads as the editor saying the
/// statement will work.
/// </para>
/// </summary>
public sealed class SqlVocabulary {
    public SqlVocabulary(
        IEnumerable<string> keywords,
        IEnumerable<string> functions,
        IEnumerable<string> types,
        IReadOnlyDictionary<string, string> docs = null) {
        Keywords = new HashSet<string>(keywords, System.StringComparer.OrdinalIgnoreCase);
        Functions = new HashSet<string>(functions, System.StringComparer.OrdinalIgnoreCase);
        Types = new HashSet<string>(types, System.StringComparer.OrdinalIgnoreCase);
        Docs = docs ?? new Dictionary<string, string>();
    }

    public HashSet<string> Keywords { get; }
    public HashSet<string> Functions { get; }
    public HashSet<string> Types { get; }
    public IReadOnlyDictionary<string, string> Docs { get; }

    /// <summary>What the hover text calls this dialect ("T-SQL", "Oracle SQL").</summary>
    public string Label { get; init; } = "SQL";

    // The statement words every dialect in this set shares. Split out so a new
    // dialect starts from the parts of SQL that are actually standard rather than
    // from a copy of T-SQL with the Microsoft-only words left in by accident.
    private static readonly string[] _coreKeywords = {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "MERGE", "USING", "MATCHED", "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "OUTER", "CROSS", "ON", "AS", "DISTINCT", "UNION", "ALL", "EXCEPT", "INTERSECT",
        "WITH", "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT", "IN", "EXISTS",
        "BETWEEN", "LIKE", "IS", "NULL", "ASC", "DESC", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "TABLE", "VIEW", "PROCEDURE", "FUNCTION", "INDEX",
        "TRIGGER", "SCHEMA", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "CONSTRAINT", "UNIQUE",
        "CHECK", "DEFAULT", "GRANT", "REVOKE", "COMMIT", "ROLLBACK", "SAVEPOINT", "CAST",
        "OVER", "PARTITION", "ROW", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT",
    };

    private static readonly string[] _coreFunctions = {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "ABS", "CEIL", "FLOOR",
        "ROUND", "MOD", "POWER", "SQRT", "UPPER", "LOWER", "TRIM", "SUBSTRING", "LENGTH",
        "POSITION", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
    };

    private static readonly string[] _coreTypes = {
        "CHARACTER", "CHAR", "VARCHAR", "CLOB", "BLOB", "NUMERIC", "DECIMAL", "INTEGER", "INT",
        "SMALLINT", "BIGINT", "FLOAT", "REAL", "DOUBLE", "PRECISION", "BOOLEAN", "DATE", "TIME",
        "TIMESTAMP", "INTERVAL",
    };

    private static IEnumerable<string> Core(string[] core, params string[] extra) {
        foreach (var word in core) {
            yield return word;
        }
        foreach (var word in extra) {
            yield return word;
        }
    }

    /// <summary>Microsoft T-SQL — what <c>#!sql</c> has always completed.</summary>
    public static SqlVocabulary TSql { get; } = new SqlVocabulary(
        Core(_coreKeywords,
            "TARGET", "SOURCE", "APPLY", "TOP", "PERCENT", "PROC", "DATABASE", "IDENTITY",
            "CLUSTERED", "NONCLUSTERED", "DECLARE", "BEGIN", "TRANSACTION", "TRAN", "TRY", "CATCH",
            "THROW", "RAISERROR", "RETURN", "EXEC", "EXECUTE", "GO", "OUTPUT", "WITHIN", "GROUPING",
            "ROLLUP", "CUBE", "PIVOT", "UNPIVOT", "COLLATE", "CONVERT", "IF", "WHILE", "BREAK",
            "CONTINUE"),
        Core(_coreFunctions,
            "COUNT_BIG", "STDEV", "VAR", "STRING_AGG", "GROUPING_ID", "GETDATE", "GETUTCDATE",
            "SYSDATETIME", "SYSUTCDATETIME", "DATEADD", "DATEDIFF", "DATEPART", "DATENAME", "DAY",
            "MONTH", "YEAR", "EOMONTH", "FORMAT", "ISNULL", "IIF", "LEN", "DATALENGTH", "CHARINDEX",
            "PATINDEX", "REPLACE", "STUFF", "LTRIM", "RTRIM", "CONCAT", "CONCAT_WS", "LEFT", "RIGHT",
            "REPLICATE", "REVERSE", "SPACE", "CEILING", "SIGN", "RAND", "NEWID", "TRY_CAST",
            "TRY_CONVERT", "TRY_PARSE", "PARSE", "CUME_DIST", "PERCENT_RANK", "OBJECT_ID",
            "SCOPE_IDENTITY", "IDENT_CURRENT", "ISNUMERIC", "ISDATE", "JSON_VALUE", "JSON_QUERY",
            "OPENJSON"),
        Core(_coreTypes,
            "TINYINT", "BIT", "MONEY", "SMALLMONEY", "DATETIME", "DATETIME2", "SMALLDATETIME",
            "DATETIMEOFFSET", "NCHAR", "NVARCHAR", "TEXT", "NTEXT", "BINARY", "VARBINARY", "IMAGE",
            "UNIQUEIDENTIFIER", "XML", "SQL_VARIANT", "ROWVERSION", "GEOGRAPHY", "GEOMETRY",
            "HIERARCHYID"),
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
            ["SELECT"] = "**SELECT** — retrieves rows.\n\n`SELECT col1, col2 FROM table WHERE predicate`",
            ["MERGE"] = "**MERGE** — insert/update/delete a target from a source in one statement.\n\n`MERGE target USING source ON (...) WHEN MATCHED THEN UPDATE ... WHEN NOT MATCHED THEN INSERT ...;`",
            ["JOIN"] = "**JOIN** — combines rows from two tables on a predicate. Prefix with INNER / LEFT / RIGHT / FULL / CROSS.",
            ["ISNULL"] = "**ISNULL(check, replacement)** — returns `replacement` when `check` is NULL.",
            ["COALESCE"] = "**COALESCE(a, b, ...)** — returns the first non-NULL argument.",
            ["ROW_NUMBER"] = "**ROW_NUMBER() OVER(...)** — sequential number per partition.\n\n`ROW_NUMBER() OVER (PARTITION BY g ORDER BY k)`",
            ["DATEADD"] = "**DATEADD(datepart, number, date)** — adds an interval to a date.",
            ["DATEDIFF"] = "**DATEDIFF(datepart, start, end)** — difference between two dates in datepart units.",
            ["CAST"] = "**CAST(expr AS type)** — converts an expression to a data type.",
            ["CONVERT"] = "**CONVERT(type, expr [, style])** — converts with an optional style code.",
            ["STRING_AGG"] = "**STRING_AGG(expr, sep)** — concatenates values with a separator (add `WITHIN GROUP (ORDER BY ...)`).",
        }) { Label = "T-SQL" };

    /// <summary>Oracle SQL and the PL/SQL words that show up in a query cell.</summary>
    public static SqlVocabulary OracleSql { get; } = new SqlVocabulary(
        Core(_coreKeywords,
            "DUAL", "CONNECT", "START", "PRIOR", "MINUS", "SIBLINGS", "NULLS", "FIRST", "LAST",
            "PARTITION", "PIVOT", "UNPIVOT", "MODEL", "SAMPLE", "SEQUENCE", "SYNONYM", "PACKAGE",
            "BODY", "DECLARE", "BEGIN", "EXCEPTION", "LOOP", "CURSOR", "RETURNING", "PRAGMA",
            "REPLACE", "PURGE", "TABLESPACE", "NOLOGGING", "PCTFREE", "STORAGE", "MATERIALIZED",
            "KEEP", "IGNORE", "RESPECT"),
        Core(_coreFunctions,
            "NVL", "NVL2", "DECODE", "SYSDATE", "SYSTIMESTAMP", "TO_CHAR", "TO_DATE", "TO_NUMBER",
            "TO_TIMESTAMP", "ADD_MONTHS", "MONTHS_BETWEEN", "LAST_DAY", "NEXT_DAY", "TRUNC",
            "INSTR", "SUBSTR", "LPAD", "RPAD", "LTRIM", "RTRIM", "REPLACE", "TRANSLATE", "INITCAP",
            "LISTAGG", "REGEXP_LIKE", "REGEXP_SUBSTR", "REGEXP_REPLACE", "REGEXP_INSTR",
            "GREATEST", "LEAST", "RATIO_TO_REPORT", "SYS_GUID", "USER", "ROWNUM", "ROWID"),
        Core(_coreTypes,
            "VARCHAR2", "NVARCHAR2", "NCHAR", "NUMBER", "BINARY_FLOAT", "BINARY_DOUBLE", "LONG",
            "RAW", "NCLOB", "BFILE", "XMLTYPE", "ROWID", "UROWID", "PLS_INTEGER", "BINARY_INTEGER"),
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
            ["NVL"] = "**NVL(expr, replacement)** — returns `replacement` when `expr` is NULL. Oracle's ISNULL.",
            ["DECODE"] = "**DECODE(expr, search, result, …, default)** — inline CASE, Oracle style.",
            ["DUAL"] = "**DUAL** — Oracle's one-row table, for selecting an expression: `SELECT SYSDATE FROM DUAL`.",
            ["TO_DATE"] = "**TO_DATE(text, format)** — parses text to a DATE.\n\n`TO_DATE('2026-08-25', 'YYYY-MM-DD')`",
            ["TO_CHAR"] = "**TO_CHAR(value [, format])** — formats a number or date as text.",
            ["LISTAGG"] = "**LISTAGG(expr, sep) WITHIN GROUP (ORDER BY …)** — concatenates values across rows.",
            ["ROWNUM"] = "**ROWNUM** — the row's position in the result *before* ORDER BY. Use a subquery or `FETCH FIRST n ROWS ONLY` for a top-N.",
        }) { Label = "Oracle SQL" };

    /// <summary>Standard SQL only — nothing that belongs to one vendor.</summary>
    public static SqlVocabulary AnsiSql { get; } = new SqlVocabulary(
        _coreKeywords, _coreFunctions, _coreTypes,
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) {
            ["SELECT"] = "**SELECT** — retrieves rows.\n\n`SELECT col1, col2 FROM table WHERE predicate`",
            ["COALESCE"] = "**COALESCE(a, b, ...)** — returns the first non-NULL argument.",
            ["EXTRACT"] = "**EXTRACT(field FROM source)** — pulls a component out of a date or interval.\n\n`EXTRACT(YEAR FROM order_date)`",
            ["FETCH"] = "**OFFSET n ROWS FETCH NEXT m ROWS ONLY** — the standard spelling of a page. Vendors also spell it LIMIT.",
        }) { Label = "SQL" };
}
