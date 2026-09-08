using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Dapper;

namespace ClrKernel.Database;

/// <summary>
/// Maps query rows to <c>T</c>: a scalar type (single column), a class with
/// settable properties, or a record / immutable type whose constructor
/// parameters match column names (case-insensitive).
///
/// <para>
/// Dapper does the materializing — it emits a deserializer per shape instead of
/// reflecting per row, and it is the mapper this code was ported from. Three
/// pieces sit around it, each because a notebook's query and a notebook's type
/// are written independently of one another:
/// </para>
/// <list type="bullet">
/// <item><see cref="ProjectedReader"/>, because Dapper's constructor mapping is
/// positional: without it <c>SELECT *</c> into a three-field record fails on the
/// column count, and a record whose fields are in a different order than the
/// SELECT fails too.</item>
/// <item><see cref="RelaxedConstructor"/>, because Dapper also matches the
/// parameter <em>types</em>: an <c>int</c> column will not fill a <c>long</c>
/// parameter, and a driver that returns a number as text fills nothing.</item>
/// <item>Two type handlers, for a Guid and a DateTimeOffset a driver returned as
/// text — which ODBC does.</item>
/// </list>
/// </summary>
internal static class ObjectMapper {
    private static readonly ConcurrentDictionary<Type, ConstructorInfo[]> _constructors = new();
    private static readonly ConcurrentDictionary<Type, bool> _mapped = new();
    private static readonly ConcurrentDictionary<Type, string[]> _members = new();
    private static int _handlers;

    public static IReadOnlyList<T> Map<T>(IDataReader reader) {
        AddHandlers();
        var columns = Names(reader);
        var ctor = Constructor(typeof(T), columns);
        if (ctor == null) {
            RequireSomethingToFill(typeof(T), columns);
            // A scalar or a class with settable members: Dapper matches by name in
            // any order and converts, so it gets the reader as it is.
            return reader.Parse<T>().ToList();
        }
        RelaxTypesFor(typeof(T));
        var ordinals = Ordinals(reader, ctor);
        return new ProjectedReader(reader, ordinals).Parse<T>().ToList();
    }

    public static IReadOnlyList<T> Map<T>(DataTable table) {
        // The same path as a reader, so the two overloads cannot drift.
        using var reader = table.CreateDataReader();
        return Map<T>(reader);
    }

    private static string[] Names(IDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

    /// <summary>
    /// The constructor to build <c>T</c> with, or null when <c>T</c> is not built
    /// that way. The longest one whose parameters all have a column, which is how
    /// a record is matched — by name, in any order, ignoring columns it has no
    /// parameter for.
    /// </summary>
    private static ConstructorInfo Constructor(Type type, string[] columns) {
        if (ValueConverter.IsScalar(type) || type == typeof(object)) {
            return null;
        }
        var candidates = _constructors.GetOrAdd(type, t => t.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToArray());
        var names = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(c => c.GetParameters().All(p => names.Contains(p.Name)));
    }

    /// <summary>Where each of the constructor's parameters is in the reader.</summary>
    private static int[] Ordinals(IDataReader reader, ConstructorInfo ctor) {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) {
            // First wins: `SELECT a.Id, b.Id` is a real query, and a dictionary
            // built by adding both is an exception rather than a result — which is
            // exactly what this mapper used to throw on a join.
            var name = reader.GetName(i);
            if (!columns.ContainsKey(name)) {
                columns[name] = i;
            }
        }
        return ctor.GetParameters().Select(p => columns[p.Name]).ToArray();
    }

    /// <summary>
    /// Refuses a type that no column can fill, rather than handing back a row of
    /// defaults.
    ///
    /// <para>
    /// The failure this prevents: <c>SELECT OrderDate</c> into a type whose one
    /// member is <c>CheckpointValue</c> returned <c>1/1/0001</c> — a wrong answer
    /// wearing the shape of a right one, and nothing on screen to say the two
    /// names never met. Partial matches stay legal; a type is often wider than one
    /// query. Zero is the case that is always a mistake.
    /// </para>
    /// </summary>
    private static void RequireSomethingToFill(Type type, string[] columns) {
        if (ValueConverter.IsScalar(type) || type == typeof(object) || columns.Length == 0) {
            return;
        }
        var members = _members.GetOrAdd(type, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name)
            .Concat(t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name))
            .ToArray());
        if (members.Length == 0
            || members.Any(m => columns.Contains(m, StringComparer.OrdinalIgnoreCase))) {
            return;
        }
        throw new InvalidOperationException(
            $"No column matches any member of {type.Name}, so every row would come back empty. "
            + $"The query returned {string.Join(", ", columns)}; {type.Name} has "
            + $"{string.Join(", ", members)}. Columns and members are matched by name — "
            + "alias the column in the query (SELECT OrderDate AS CheckpointValue) or rename the member.");
    }

    private static void AddHandlers() {
        if (System.Threading.Interlocked.Exchange(ref _handlers, 1) != 0) {
            return;
        }
        SqlMapper.AddTypeHandler(new ParsingHandler<Guid>(s => Guid.Parse(s)));
        SqlMapper.AddTypeHandler(new ParsingHandler<DateTimeOffset>(s => DateTimeOffset.Parse(s)));
        // A `date` column is a DateTime in every provider here, and DateOnly is
        // what somebody writes when the time is not part of the answer. Dapper
        // converts neither of these itself.
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
    }

    private static void RelaxTypesFor(Type type) =>
        _mapped.GetOrAdd(type, t => {
            SqlMapper.SetTypeMap(t, new RelaxedConstructor(t));
            return true;
        });

    /// <summary>
    /// Dapper's own type map with the constructor's parameter <em>types</em> no
    /// longer required to equal the column types.
    ///
    /// <para>
    /// The names still have to line up one for one, which <see cref="ProjectedReader"/>
    /// has already arranged. Only the types are relaxed, and only so far as Dapper
    /// converting each value — which it does per column anyway.
    /// </para>
    /// </summary>
    private sealed class RelaxedConstructor : SqlMapper.ITypeMap {
        private readonly Type _type;
        private readonly SqlMapper.ITypeMap _default;

        public RelaxedConstructor(Type type) {
            _type = type;
            _default = new DefaultTypeMap(type);
        }

        public ConstructorInfo FindConstructor(string[] names, Type[] types) =>
            _type.GetConstructors()
                .Where(c => c.GetParameters().Length == names.Length
                    && c.GetParameters()
                        .Select((p, i) => string.Equals(p.Name, names[i], StringComparison.OrdinalIgnoreCase))
                        .All(matched => matched))
                .FirstOrDefault()
            ?? _default.FindConstructor(names, types);

        public ConstructorInfo FindExplicitConstructor() => _default.FindExplicitConstructor();

        public SqlMapper.IMemberMap GetConstructorParameter(ConstructorInfo ctor, string columnName) =>
            _default.GetConstructorParameter(ctor, columnName);

        public SqlMapper.IMemberMap GetMember(string columnName) => _default.GetMember(columnName);
    }

    /// <summary>A `date` column, or a date held as text, into a <see cref="DateOnly"/>.</summary>
    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly> {
        public override DateOnly Parse(object value) => value switch {
            DateOnly only => only,
            DateTime moment => DateOnly.FromDateTime(moment),
            string text => DateOnly.Parse(text),
            _ => DateOnly.FromDateTime(Convert.ToDateTime(value)),
        };

        // As a DateTime, not a DateOnly: not every provider here binds one.
        public override void SetValue(IDbDataParameter parameter, DateOnly value) =>
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>A `time` column into a <see cref="TimeOnly"/>.</summary>
    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly> {
        public override TimeOnly Parse(object value) => value switch {
            TimeOnly only => only,
            TimeSpan span => TimeOnly.FromTimeSpan(span),
            DateTime moment => TimeOnly.FromDateTime(moment),
            string text => TimeOnly.Parse(text),
            _ => TimeOnly.FromTimeSpan((TimeSpan)value),
        };

        public override void SetValue(IDbDataParameter parameter, TimeOnly value) =>
            parameter.Value = value.ToTimeSpan();
    }

    /// <summary>A value a driver handed back as text where a CLR type was wanted.</summary>
    private sealed class ParsingHandler<T> : SqlMapper.TypeHandler<T> {
        private readonly Func<string, T> _parse;

        public ParsingHandler(Func<string, T> parse) => _parse = parse;

        public override T Parse(object value) =>
            value is T typed ? typed : _parse(value.ToString());

        public override void SetValue(IDbDataParameter parameter, T value) => parameter.Value = value;
    }
}
