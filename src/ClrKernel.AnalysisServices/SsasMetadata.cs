using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;

namespace ClrKernel.AnalysisServices;
/// <summary>A partition identified by table + name (for processing/removal).</summary>
public interface IPartition {
    string TableName { get; }
    string PartitionName { get; }
    string DataSourceName { get; }
    string Query { get; }
}

/// <summary>A partition definition used to add/update or process a partition.</summary>
public sealed class PartitionDefinition : IPartition {
    public PartitionDefinition(string tableName, string partitionName, string dataSourceName = "", string query = "") {
        TableName = tableName;
        PartitionName = partitionName;
        DataSourceName = dataSourceName;
        Query = query;
    }
    public string TableName { get; }
    public string PartitionName { get; }
    public string DataSourceName { get; }
    public string Query { get; }

    public static implicit operator PartitionDefinition((string TableName, string PartitionName) t) =>
        new PartitionDefinition(t.TableName, t.PartitionName);
    public static implicit operator PartitionDefinition((string TableName, string PartitionName, string DataSourceName, string Query) t) =>
        new PartitionDefinition(t.TableName, t.PartitionName, t.DataSourceName, t.Query);
}

/// <summary>Table metadata (name + row/partition counts) from the model DMVs.</summary>
public sealed class SsasTable {
    public string Database { get; set; }
    public string TableName { get; set; }
    public long PartitionCount { get; set; }
    public long RecordCount { get; set; }
    public string Description { get; set; }
    public DateTime? RefreshedTime { get; set; }
    public override string ToString() => $"{TableName} ({RecordCount:N0} rows, {PartitionCount} partition(s))";
}

/// <summary>Partition metadata (name, source, row count) from the model DMVs.</summary>
public sealed class SsasPartition : IPartition {
    public string Database { get; set; }
    public string TableName { get; set; }
    public string PartitionName { get; set; }
    public string DataSourceName { get; set; }
    public long RecordCount { get; set; }
    public string QueryDefinition { get; set; }
    public DateTime? RefreshedTime { get; set; }
    public string ErrorMessage { get; set; }

    string IPartition.TableName => TableName;
    string IPartition.PartitionName => PartitionName;
    string IPartition.DataSourceName => DataSourceName;
    string IPartition.Query => QueryDefinition;
    public override string ToString() => $"{TableName}/{PartitionName} ({RecordCount:N0} rows)";
}

// --- Raw DMV row shapes ($SYSTEM.TMSCHEMA_*) --------------------------

internal sealed class DmvTable {
    public long? ID { get; set; }
    public string TableName { get; set; }
    public string Name { set => TableName = value; }
    public string Description { get; set; }
    public bool? IsHidden { get; set; }
    public DateTime? RefreshedTime { get; set; }
}

internal sealed class DmvPartition {
    public long? ID { get; set; }
    public long? TableID { get; set; }
    public string PartitionName { get; set; }
    public string Name { set => PartitionName = value; }
    public ulong? DataSourceID { get; set; }
    public string QueryDefinition { get; set; }
    public DateTime? RefreshedTime { get; set; }
    public string ErrorMessage { get; set; }
}

internal sealed class DmvPartitionStorage {
    public long? ID { get; set; }
    public long? PartitionID { get; set; }
}

internal sealed class DmvSegmentMapStorage {
    public long? ID { get; set; }
    public long? PartitionStorageID { get; set; }
    public long? RecordCount { get; set; }
}

internal sealed class DmvDataSource {
    public ulong? ID { get; set; }
    public string Name { get; set; }
}

/// <summary>Maps an ADO.NET reader (ADOMD DMV results) to records by property name.</summary>
internal static class DmvMapper {
    public static List<T> Map<T>(IDataReader reader) where T : new() {
        var props = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var columns = new List<(int ordinal, PropertyInfo prop)>();
        for (var i = 0; i < reader.FieldCount; i++) {
            if (props.TryGetValue(reader.GetName(i), out var prop)) {
                columns.Add((i, prop));
            }
        }

        var rows = new List<T>();
        while (reader.Read()) {
            var row = new T();
            foreach (var (ordinal, prop) in columns) {
                var value = reader.GetValue(ordinal);
                if (value == null || value is DBNull) {
                    continue;
                }
                prop.SetValue(row, Coerce(value, prop.PropertyType));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static object Coerce(object value, Type target) {
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(value)) {
            return value;
        }
        try {
            return Convert.ChangeType(value, t);
        } catch {
            return t == typeof(string) ? value.ToString() : null;
        }
    }
}
