using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ClrKernel.Core.Primitives;
using Adomd = Microsoft.AnalysisServices.AdomdClient;

namespace ClrKernel.AnalysisServices;
/// <summary>
/// A live handle to an Analysis Services (Tabular) model: DAX/DMV queries via
/// ADOMD.NET and metadata reads, plus processing/partition management (in the
/// partial in SsasConnection.Processing.cs). Created via <see cref="Ssas"/>.
/// </summary>
public sealed partial class SsasConnection {
    private readonly SsasConnectionSpec _spec;

    public SsasConnection(SsasConnectionSpec spec) {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
    }

    public SsasConnectionSpec Spec => _spec;

    /// <summary>Max rows materialized in a query grid (remaining rows still counted).</summary>
    public int RowLimit { get; set; } = 1000;

    private Adomd.AdomdConnection OpenAdomd() {
        var connectionString = _spec.BuildAdomdConnectionString();
        if (_spec.Auth == SsasAuthMode.AzureAd && _spec.TokenProvider != null) {
            // ADOMD.NET takes an Entra access token as the connection-string password.
            var token = _spec.TokenProvider();
            if (!connectionString.EndsWith(";", StringComparison.Ordinal)) {
                connectionString += ";";
            }
            connectionString += "Password=" + token.Token + ";";
        }
        var connection = new Adomd.AdomdConnection(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Runs a DAX (or DMV) query and returns the rows as dictionaries.</summary>
    public List<Dictionary<string, object>> QueryRows(string dax) {
        using var connection = OpenAdomd();
        using var command = connection.CreateCommand();
        command.CommandText = dax;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object>>();
        while (reader.Read()) {
            var row = new Dictionary<string, object>();
            for (var i = 0; i < reader.FieldCount; i++) {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Runs a DAX query and returns an interactive result grid.</summary>
    public DisplayData Query(string dax) {
        using var connection = OpenAdomd();
        using var command = connection.CreateCommand();
        command.CommandText = dax;
        using var reader = command.ExecuteReader();
        var (html, total) = RenderGrid(reader);
        return new DisplayData($"[{total} row(s)]", html);
    }

    private (string html, int total) RenderGrid(IDataReader reader) {
        var fieldCount = reader.FieldCount;
        var columns = new string[fieldCount];
        var types = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++) {
            columns[i] = CleanColumn(reader.GetName(i));
            Type fieldType;
            try { fieldType = reader.GetFieldType(i); } catch { fieldType = typeof(string); }
            types[i] = InteractiveTable.KindOf(fieldType);
        }
        var rows = new List<IReadOnlyList<string>>();
        var total = 0;
        while (reader.Read()) {
            total++;
            if (rows.Count >= RowLimit) {
                continue;
            }
            var row = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++) {
                row[i] = InteractiveTable.CellText(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return (InteractiveTable.Render(columns, rows, types, total), total);
    }

    // DAX/DMV columns often come back as "[Table[Column]]" or "[Measure]" — strip
    // the surrounding brackets for a cleaner grid header.
    private static string CleanColumn(string name) {
        if (string.IsNullOrEmpty(name)) {
            return name;
        }
        var n = name;
        if (n.StartsWith("[") && n.EndsWith("]")) {
            n = n.Substring(1, n.Length - 2);
        }
        return n;
    }

    private List<T> Dmv<T>(Adomd.AdomdConnection connection, string query) where T : new() {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        return DmvMapper.Map<T>(reader);
    }

    /// <summary>Table metadata (row counts, partition counts) from the model DMVs.</summary>
    public List<SsasTable> Tables() {
        var database = _spec.Database ?? "";
        using var connection = OpenAdomd();
        var tables = Dmv<DmvTable>(connection, "select * from $system.TMSCHEMA_TABLES");
        var partitions = Dmv<DmvPartition>(connection, "select * from $system.TMSCHEMA_PARTITIONS");
        var storages = Dmv<DmvPartitionStorage>(connection, "select * from $system.TMSCHEMA_PARTITION_STORAGES");
        var segments = Dmv<DmvSegmentMapStorage>(connection, "select * from $system.TMSCHEMA_SEGMENT_MAP_STORAGES");

        return (
            from table in tables
            join partition in partitions on table.ID equals partition.TableID
            join storage in storages on partition.ID equals storage.PartitionID
            join segment in segments on storage.ID equals segment.PartitionStorageID
            group new { table, partition, records = segment.RecordCount ?? 0 } by table.ID into g
            select new SsasTable {
                Database = database,
                TableName = g.First().table.TableName,
                Description = g.First().table.Description,
                RefreshedTime = g.First().table.RefreshedTime,
                PartitionCount = g.Select(x => x.partition.ID).Distinct().Count(),
                RecordCount = g.Sum(x => x.records),
            }).OrderBy(t => t.TableName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Partition metadata (source, row count, refresh time) from the model DMVs.</summary>
    public List<SsasPartition> Partitions() {
        var database = _spec.Database ?? "";
        using var connection = OpenAdomd();
        var dataSources = Dmv<DmvDataSource>(connection, "select * from $system.TMSCHEMA_DATA_SOURCES");
        var tables = Dmv<DmvTable>(connection, "select * from $system.TMSCHEMA_TABLES");
        var partitions = Dmv<DmvPartition>(connection, "select * from $system.TMSCHEMA_PARTITIONS");
        var storages = Dmv<DmvPartitionStorage>(connection, "select * from $system.TMSCHEMA_PARTITION_STORAGES");
        var segments = Dmv<DmvSegmentMapStorage>(connection, "select * from $system.TMSCHEMA_SEGMENT_MAP_STORAGES");

        var tableById = tables.ToDictionary(t => t.ID ?? -1, t => t.TableName);
        var dsById = dataSources.ToDictionary(d => (long)(d.ID ?? 0), d => d.Name);
        var recordsByPartition = (
            from storage in storages
            join segment in segments on storage.ID equals segment.PartitionStorageID
            group segment.RecordCount ?? 0 by storage.PartitionID into g
            select new { PartitionID = g.Key, Records = g.Sum() }
        ).ToDictionary(x => x.PartitionID ?? -1, x => x.Records);

        return partitions.Select(p => new SsasPartition {
            Database = database,
            TableName = tableById.TryGetValue(p.TableID ?? -1, out var tn) ? tn : "",
            PartitionName = p.PartitionName,
            DataSourceName = p.DataSourceID.HasValue && dsById.TryGetValue((long)p.DataSourceID.Value, out var ds) ? ds : null,
            RecordCount = recordsByPartition.TryGetValue(p.ID ?? -1, out var rc) ? rc : 0,
            QueryDefinition = p.QueryDefinition,
            RefreshedTime = p.RefreshedTime,
            ErrorMessage = p.ErrorMessage,
        }).OrderBy(p => p.TableName, StringComparer.OrdinalIgnoreCase)
          .ThenBy(p => p.PartitionName, StringComparer.OrdinalIgnoreCase)
          .ToList();
    }
}
