using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Primitives;
using AMO = Microsoft.AnalysisServices;
using Tabular = Microsoft.AnalysisServices.Tabular;

namespace ClrKernel.AnalysisServices;
/// <summary>Processing (refresh) and partition management via the Tabular Object Model.</summary>
public sealed partial class SsasConnection {
    /// <summary>Default parallelism for a process operation.</summary>
    public int MaxParallelism { get; set; } = 8;

    private Tabular.Server OpenTomServer() {
        var server = new Tabular.Server();
        if (_spec.Auth == SsasAuthMode.AzureAd && _spec.TokenProvider != null) {
            var token = _spec.TokenProvider();
            server.AccessToken = new AMO.AccessToken(token.Token, token.ExpiresOn);
            server.OnAccessTokenExpired = _ => {
                var t = _spec.TokenProvider();
                return new AMO.AccessToken(t.Token, t.ExpiresOn);
            };
        }
        server.Connect(_spec.BuildTomConnectionString());
        return server;
    }

    private Tabular.Model OpenModel(Tabular.Server server) {
        var db = server.Databases[_spec.Database]
            ?? throw new Exception($"Analysis Services database '{_spec.Database}' not found on '{_spec.Server}'.");
        return db.Model;
    }

    /// <summary>Recalculates the whole model (RefreshType.Calculate).</summary>
    public DisplayData Recalculate() {
        using var server = OpenTomServer();
        var model = OpenModel(server);
        model.RequestRefresh(Tabular.RefreshType.Calculate);
        return Save(model, "Recalculate", 0);
    }

    /// <summary>Processes the entire model (default: a full refresh).</summary>
    public DisplayData ProcessModel(SsasRefresh refresh = SsasRefresh.Full) {
        using var server = OpenTomServer();
        var model = OpenModel(server);
        model.RequestRefresh(Ssas.ToTabular(refresh));
        return Save(model, $"Process model ({refresh})", MaxParallelism);
    }

    /// <summary>Processes one or more tables.</summary>
    public DisplayData ProcessTables(IEnumerable<string> tableNames, SsasRefresh refresh = SsasRefresh.Full, int maxParallelism = 0) {
        using var server = OpenTomServer();
        var model = OpenModel(server);
        var rt = Ssas.ToTabular(refresh);
        var missing = new List<string>();
        foreach (var name in tableNames) {
            var table = model.Tables.Find(name);
            if (table == null) {
                missing.Add(name);
            } else {
                table.RequestRefresh(rt);
            }
        }
        if (missing.Count > 0) {
            throw new Exception("ProcessTables: tables not found: " + string.Join(", ", missing));
        }
        return Save(model, $"Process tables ({refresh})", maxParallelism > 0 ? maxParallelism : MaxParallelism);
    }

    /// <summary>Processes tables by name (params overload).</summary>
    public DisplayData ProcessTables(params string[] tableNames) => ProcessTables(tableNames, SsasRefresh.Full);

    /// <summary>Processes a set of partitions (with optional per-partition query overrides).</summary>
    public DisplayData ProcessPartitions(IEnumerable<PartitionDefinition> partitions, SsasRefresh refresh = SsasRefresh.Full, int maxParallelism = 0) {
        using var server = OpenTomServer();
        var model = OpenModel(server);
        RequestPartitionRefresh(model, partitions, Ssas.ToTabular(refresh));
        return Save(model, $"Process partitions ({refresh})", maxParallelism > 0 ? maxParallelism : MaxParallelism);
    }

    /// <summary>Processes partitions given as (table, partition) tuples.</summary>
    public DisplayData ProcessPartitions(IEnumerable<(string TableName, string PartitionName)> partitions, SsasRefresh refresh = SsasRefresh.Full, int maxParallelism = 0) =>
        ProcessPartitions(partitions.Select(p => (PartitionDefinition)p), refresh, maxParallelism);

    private static void RequestPartitionRefresh(Tabular.Model model, IEnumerable<IPartition> partitions, Tabular.RefreshType refreshType) {
        var resolved = (
            from p in partitions
            let table = model.Tables.Find(p.TableName)
            let partition = table?.Partitions?.Find(p.PartitionName)
            select new {
                Description = $"- Table: {p.TableName}, Partition: {p.PartitionName}",
                Partition = partition,
                Overrides = partition == null ? null : GeneratePartitionOverrides(partition, p),
            }).ToList();

        var missing = resolved.Where(r => r.Partition == null).ToList();
        if (missing.Count > 0) {
            throw new Exception("ProcessPartitions: partitions not found:\n" + string.Join("\n", missing.Select(m => m.Description)));
        }

        foreach (var r in resolved) {
            if (r.Overrides != null) {
                r.Partition.RequestRefresh(refreshType, new[] { r.Overrides });
            } else {
                r.Partition.RequestRefresh(refreshType);
            }
        }
    }

    private static Tabular.DataRefresh.OverrideCollection GeneratePartitionOverrides(Tabular.Partition partition, IPartition definition) {
        if (string.IsNullOrEmpty(definition.Query)) {
            return null;
        }
        if (partition.Source is not Tabular.QueryPartitionSource src) {
            return null;
        }
        if (definition.Query == src.Query) {
            return null;
        }
        var dataSource = src.DataSource;
        if (!string.IsNullOrEmpty(definition.DataSourceName) && definition.DataSourceName != dataSource.Name) {
            dataSource = partition.Model.DataSources.Find(definition.DataSourceName)
                ?? throw new Exception($"GeneratePartitionOverrides('{definition.TableName}','{definition.PartitionName}'): invalid data source '{definition.DataSourceName}'.");
        }
        var queryOverride = new Tabular.DataRefresh.QueryPartitionSourceOverride { DataSource = dataSource, Query = definition.Query };
        var partitionOverride = new Tabular.DataRefresh.PartitionOverride { OriginalObject = partition, Source = queryOverride };
        return new Tabular.DataRefresh.OverrideCollection { Partitions = { partitionOverride } };
    }

    // --- Partition management ---------------------------------------------

    /// <summary>Adds a partition if missing, or updates its query if changed.</summary>
    public DisplayData EnsurePartition(string tableName, string partitionName, string dataSourceName, string query, bool autoRecalc = true) =>
        EnsurePartitions(new[] { new PartitionDefinition(tableName, partitionName, dataSourceName, query) }, autoRecalc);

    /// <summary>Adds/updates a set of partitions.</summary>
    public DisplayData EnsurePartitions(IEnumerable<PartitionDefinition> partitions, bool autoRecalc = true) {
        using var server = OpenTomServer();
        var model = OpenModel(server);

        var needsRecalc = false;
        var shouldSave = false;
        foreach (var p in partitions) {
            var dataSource = model.DataSources.Find(p.DataSourceName ?? "");
            var table = model.Tables.Find(p.TableName)
                ?? throw new Exception($"EnsurePartitions: table '{p.TableName}' not found.");
            if (dataSource == null && !string.IsNullOrEmpty(p.DataSourceName)) {
                throw new Exception($"EnsurePartitions: data source '{p.DataSourceName}' not found.");
            }
            var partition = table.Partitions.Find(p.PartitionName);
            var source = BuildPartitionSource(dataSource, p.Query);

            if (partition != null && partition.Source.GetType() != source.GetType()) {
                throw new Exception($"EnsurePartitions: cannot change the data-source type of partition '{p.PartitionName}' on '{p.TableName}'.");
            }
            if (partition == null) {
                table.Partitions.Add(new Tabular.Partition { Name = p.PartitionName, Source = source });
                shouldSave = needsRecalc = true;
            } else if (Normalize(PartitionQuery(partition.Source)) != Normalize(PartitionQuery(source))) {
                partition.Source = source;
                shouldSave = true;
            }
        }

        if (needsRecalc && autoRecalc) {
            model.RequestRefresh(Tabular.RefreshType.Calculate);
        }
        return shouldSave ? Save(model, "Ensure partitions", 0) : new DisplayData("Ensure partitions: no changes.");
    }

    /// <summary>Removes a partition if present.</summary>
    public DisplayData RemovePartition(string tableName, string partitionName, bool autoRecalc = true) =>
        RemovePartitions(new[] { new PartitionDefinition(tableName, partitionName) }, autoRecalc);

    /// <summary>Removes a set of partitions.</summary>
    public DisplayData RemovePartitions(IEnumerable<PartitionDefinition> partitions, bool autoRecalc = true) {
        using var server = OpenTomServer();
        var model = OpenModel(server);
        var shouldSave = false;
        foreach (var p in partitions) {
            var table = model.Tables.Find(p.TableName)
                ?? throw new Exception($"RemovePartitions: table '{p.TableName}' not found.");
            var partition = table.Partitions.Find(p.PartitionName);
            if (partition == null) {
                continue;
            }
            table.Partitions.Remove(partition);
            shouldSave = true;
        }
        if (autoRecalc) {
            model.RequestRefresh(Tabular.RefreshType.Calculate);
        }
        return shouldSave ? Save(model, "Remove partitions", 0) : new DisplayData("Remove partitions: no changes.");
    }

    private static Tabular.PartitionSource BuildPartitionSource(Tabular.DataSource dataSource, string query) => dataSource switch {
        null => new Tabular.MPartitionSource { Expression = query },
        Tabular.StructuredDataSource => new Tabular.MPartitionSource { Expression = query },
        Tabular.ProviderDataSource => new Tabular.QueryPartitionSource { DataSource = dataSource, Query = query },
        _ => throw new Exception($"Unsupported data source type: {dataSource.GetType()}"),
    };

    private static string PartitionQuery(Tabular.PartitionSource source) => source switch {
        Tabular.QueryPartitionSource qs => qs.Query ?? "",
        Tabular.MPartitionSource ms => ms.Expression ?? "",
        _ => throw new Exception($"Unsupported partition source type: {source?.GetType()}"),
    };

    private static string Normalize(string query) => query.Replace("\r\n", "\n").Replace("\t", " ");

    private DisplayData Save(Tabular.Model model, string operation, int maxParallelism) {
        var result = maxParallelism > 0
            ? model.SaveChanges(new Tabular.SaveOptions { MaxParallelism = maxParallelism })
            : model.SaveChanges();
        var errors = DescribeErrors(result);
        if (errors != null) {
            throw new Exception($"SSAS {operation} reported errors: {errors}");
        }
        return new DisplayData($"{operation} complete on {_spec.Describe()}.");
    }

    private static string DescribeErrors(Tabular.ModelOperationResult result) {
        try {
            var xmla = result?.XmlaResults;
            if (xmla == null || !xmla.ContainsErrors) {
                return null;
            }
            var messages = new List<string>();
            foreach (AMO.XmlaResult r in xmla) {
                foreach (AMO.XmlaMessage m in r.Messages) {
                    messages.Add(m.Description);
                }
            }
            return string.Join("; ", messages);
        } catch {
            return null; // if the result shape differs, treat as success
        }
    }
}
