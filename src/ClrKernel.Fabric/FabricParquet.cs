using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;

namespace ClrKernel.Fabric;

/// <summary>Row count and columns written to a Parquet stream.</summary>
public sealed class ParquetWriteResult {
    public int RowCount { get; set; }
    public string[] Columns { get; set; }
}

/// <summary>
/// Streams an <see cref="IDataReader"/> to a Parquet file (Parquet.Net). Mirrors
/// the ParquetTarget helper: the reader is exposed as a lazily-read collection of
/// dictionaries so ParquetSerializer can write it a row group at a time without
/// buffering the whole result set.
/// </summary>
public static class FabricParquet {
    public static async Task<ParquetWriteResult> WriteAsync(
        IDataReader reader, Stream output, int rowGroupSize = 5000, CancellationToken cancellationToken = default) {
        if (rowGroupSize <= 0) {
            throw new ArgumentException("rowGroupSize must be positive.", nameof(rowGroupSize));
        }
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var schema = CreateSchema(reader, columns);
        var adapter = new ReaderCollection(reader, columns);
        var options = new ParquetSerializerOptions { RowGroupSize = rowGroupSize, CompressionMethod = CompressionMethod.Snappy };
        await ParquetSerializer.SerializeAsync(schema, adapter, output, options, cancellationToken).ConfigureAwait(false);
        return new ParquetWriteResult { RowCount = adapter.RowCount, Columns = columns };
    }

    internal static ParquetSchema CreateSchema(IDataReader reader, string[] columns) {
        var fields = new Field[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) {
            Type type;
            try {
                type = reader.GetFieldType(i) ?? typeof(string);
            } catch {
                type = typeof(string);
            }
            fields[i] = new DataField(columns[i], MakeNullable(type));
        }
        return new ParquetSchema(fields);
    }

    private static Type MakeNullable(Type t) =>
        t.IsValueType ? typeof(Nullable<>).MakeGenericType(t) : t;

    // Wraps the reader as a read-once collection of column→value dictionaries.
    internal sealed class ReaderCollection : IReadOnlyCollection<IDictionary<string, object>> {
        private readonly IDataReader _reader;
        private readonly string[] _columns;
        private bool _iterated;
        public int RowCount { get; private set; }

        public ReaderCollection(IDataReader reader, string[] columns) {
            _reader = reader;
            _columns = columns;
        }

        public int Count => RowCount;

        public IEnumerator<IDictionary<string, object>> GetEnumerator() {
            if (_iterated) {
                throw new InvalidOperationException("The data reader may only be iterated once while writing Parquet.");
            }
            _iterated = true;
            return Read().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<IDictionary<string, object>> Read() {
            var count = _columns.Length;
            while (_reader.Read()) {
                RowCount++;
                var row = new Dictionary<string, object>(count, StringComparer.Ordinal);
                for (var i = 0; i < count; i++) {
                    var v = _reader.GetValue(i);
                    row[_columns[i]] = v is DBNull ? null : v;
                }
                yield return row;
            }
        }
    }
}
