using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Database.Provider.Fabric;

/// <summary>
/// One table (or a segment of it) to reload. Used with <see cref="FabricWarehouse.ReloadBatch(IEnumerable{FabricReloadRequest}, DataSource, FabricReloadOptions)"/>:
/// <code>
/// new FabricReloadRequest("Mart", "COMPANY.Dimension.Forecast")                       // truncate, then select * from the same table on the source
/// new FabricReloadRequest("Mart", "FactSales", segmentFilter: "Year = 2026",
///     sourceQuery: "select * from Mart.FactSales where Year = 2026")                  // delete the segment, then reload it
/// </code>
/// A table name is a single identifier here — dots inside it are part of the name.
/// </summary>
public sealed class FabricReloadRequest {
    public FabricReloadRequest() { }

    public FabricReloadRequest(string schema, string table, string sourceQuery = null, string segmentFilter = null) {
        TableSchema = schema;
        TableName = table;
        SourceQuery = sourceQuery;
        SegmentFilter = segmentFilter;
    }

    /// <summary>Target schema (default <c>dbo</c>).</summary>
    public string TableSchema { get; set; } = "dbo";
    /// <summary>Target table name (unqualified; may contain dots).</summary>
    public string TableName { get; set; }
    /// <summary>A friendly label for the segment (for progress/errors); defaults to the table name.</summary>
    public string SegmentName { get; set; }
    /// <summary>An explicit statement to clear the target. Takes precedence over <see cref="SegmentFilter"/>.</summary>
    public string DeleteCommand { get; set; }
    /// <summary>A WHERE predicate used to build <c>DELETE FROM target WHERE ...</c> when no <see cref="DeleteCommand"/> is set.</summary>
    public string SegmentFilter { get; set; }
    /// <summary>
    /// The query run on the source. Null means <c>select * from</c> the target's own name on
    /// the source (when the batch is given a <see cref="DataSource"/>); with a reader
    /// factory it is informational.
    /// </summary>
    public string SourceQuery { get; set; }
    /// <summary>Create the target table from the source schema if it doesn't exist.</summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>The bracket-quoted target, e.g. <c>[Mart].[COMPANY.Dimension.Forecast]</c>.</summary>
    internal string Target =>
        string.IsNullOrWhiteSpace(TableSchema)
            ? Database.TableName.QuotePart(TableName)
            : Database.TableName.QuotePart(TableSchema) + "." + Database.TableName.QuotePart(TableName);

    internal string Label => string.IsNullOrWhiteSpace(SegmentName) ? Target : SegmentName;

    internal string EffectiveSource => string.IsNullOrWhiteSpace(SourceQuery) ? $"select * from {Target}" : SourceQuery;

    /// <summary>
    /// What clears the target before the load: the command, the filtered delete, or —
    /// for a request with neither — a truncate, because a "reload" that appends is a
    /// duplicate-row bug waiting for its second run.
    /// </summary>
    internal string EffectiveDelete() {
        if (!string.IsNullOrWhiteSpace(DeleteCommand)) {
            return DeleteCommand;
        }

        if (!string.IsNullOrWhiteSpace(SegmentFilter)) {
            return $"DELETE FROM {Target} WHERE {SegmentFilter}";
        }
        return $"TRUNCATE TABLE {Target}";
    }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(TableName)) {
            throw new InvalidOperationException("FabricReloadRequest.TableName is required.");
        }
    }
}

/// <summary>How a batch runs; every field has a default.</summary>
public sealed class FabricReloadOptions {
    /// <summary>Tables reloaded concurrently (default 4). Each runs on its own connections.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;
    /// <summary>Create any missing target table from its source's schema — the batch-wide form of <see cref="FabricReloadRequest.CreateIfMissing"/>.</summary>
    public bool CreateTableIfMissing { get; set; }
    /// <summary>Staging lakehouse override; defaults to the one set by <see cref="FabricWarehouse.WithStaging(string)"/>.</summary>
    public string StagingLakehouse { get; set; }
}

/// <summary>Outcome of reloading one segment.</summary>
public sealed class FabricReloadResult {
    public string Segment { get; set; }
    public string Table { get; set; }
    public int RowsDeleted { get; set; }
    public int RowsInserted { get; set; }
    public bool TableCreated { get; set; }
    public bool Succeeded { get; set; }
    public string Error { get; set; }
    public override string ToString() =>
        Succeeded
            ? $"{Segment}: -{RowsDeleted:N0} / +{RowsInserted:N0} → {Table}"
            : $"{Segment}: FAILED — {Error}";
}

public sealed partial class FabricWarehouse {
    /// <summary>
    /// Reloads a set of tables from a source database, up to
    /// <see cref="FabricReloadOptions.MaxDegreeOfParallelism"/> at a time: each target is
    /// cleared (truncated, or the segment deleted), then loaded from its
    /// <see cref="FabricReloadRequest.SourceQuery"/> — by default <c>select *</c> from the
    /// same-named table on <paramref name="source"/>.
    /// <code>
    /// wh.ReloadBatch([
    ///     new FabricReloadRequest("Mart", "COMPANY.Dimension.Forecast"),
    ///     new FabricReloadRequest("Mart", "COMPANY.Dimension.Instrument", "select * from Mart.[COMPANY.Dimension.Instrument]"),
    /// ], dw, new() { MaxDegreeOfParallelism = 1, CreateTableIfMissing = true });
    /// </code>
    /// One row per table comes back; a failing table reports its error and does not stop the rest.
    /// </summary>
    public IReadOnlyList<FabricReloadResult> ReloadBatch(
        IEnumerable<FabricReloadRequest> requests, DataSource source, FabricReloadOptions options = null) =>
        ReloadBatchAsync(requests, source, options).GetAwaiter().GetResult();

    /// <inheritdoc cref="ReloadBatch(IEnumerable{FabricReloadRequest}, DataSource, FabricReloadOptions)"/>
    public Task<IReadOnlyList<FabricReloadResult>> ReloadBatchAsync(
        IEnumerable<FabricReloadRequest> requests, DataSource source, FabricReloadOptions options = null,
        CancellationToken cancellationToken = default) {
        if (source is null) {
            throw new ArgumentNullException(nameof(source));
        }
        options ??= new FabricReloadOptions();
        return RunAsync(requests, req => source.Query(req.EffectiveSource).OpenReader(),
            options.MaxDegreeOfParallelism, options.StagingLakehouse, options.CreateTableIfMissing, cancellationToken);
    }

    /// <summary>
    /// The reader-factory form: <paramref name="source"/> returns a fresh <see cref="IDataReader"/>
    /// for each request. Use it when the rows come from somewhere a <see cref="DataSource"/> does not reach.
    /// </summary>
    /// <param name="requests">Segments to reload.</param>
    /// <param name="source">Factory producing a fresh <see cref="IDataReader"/> for a request's source query.</param>
    /// <param name="maxParallelism">Maximum concurrent reloads (default 4).</param>
    /// <param name="stagingLakehouse">Staging lakehouse override; defaults to the one set by <see cref="WithStaging(string)"/>.</param>
    public IReadOnlyList<FabricReloadResult> ReloadBatch(
        IEnumerable<FabricReloadRequest> requests, Func<FabricReloadRequest, IDataReader> source,
        int maxParallelism = 4, string stagingLakehouse = null) =>
        ReloadBatchAsync(requests, source, maxParallelism, stagingLakehouse).GetAwaiter().GetResult();

    /// <inheritdoc cref="ReloadBatch(IEnumerable{FabricReloadRequest}, Func{FabricReloadRequest, IDataReader}, int, string)"/>
    public Task<IReadOnlyList<FabricReloadResult>> ReloadBatchAsync(
        IEnumerable<FabricReloadRequest> requests, Func<FabricReloadRequest, IDataReader> source,
        int maxParallelism = 4, string stagingLakehouse = null, CancellationToken cancellationToken = default) {
        if (source is null) {
            throw new ArgumentNullException(nameof(source));
        }
        return RunAsync(requests, source, maxParallelism, stagingLakehouse, false, cancellationToken);
    }

    private async Task<IReadOnlyList<FabricReloadResult>> RunAsync(
        IEnumerable<FabricReloadRequest> requests, Func<FabricReloadRequest, IDataReader> source,
        int maxParallelism, string stagingLakehouse, bool createAll, CancellationToken cancellationToken) {
        if (requests is null) {
            throw new ArgumentNullException(nameof(requests));
        }

        if (maxParallelism < 1) {
            throw new ArgumentException("maxParallelism must be at least 1.", nameof(maxParallelism));
        }

        var list = requests.ToList();
        foreach (var r in list) {
            r.Validate();
        }

        var results = new FabricReloadResult[list.Count];
        var options = new ParallelOptions {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count), options, async (i, ct) => {
            var req = list[i];
            var result = new FabricReloadResult { Segment = req.Label, Table = req.Target };
            try {
                // A target that is about to be created has nothing to clear.
                if (TableExists(req.Target)) {
                    result.RowsDeleted = Math.Max(0, Execute(req.EffectiveDelete()));
                }
                using var reader = source(req)
                    ?? throw new InvalidOperationException($"Source reader for segment '{req.Label}' was null.");
                var inserted = await BulkInsertAsync(
                    reader, req.Target, createAll || req.CreateIfMissing, stagingLakehouse, ct).ConfigureAwait(false);
                result.RowsInserted = inserted.RowCount;
                result.TableCreated = inserted.TableCreated;
                result.Succeeded = true;
            } catch (Exception ex) {
                result.Succeeded = false;
                result.Error = ex.Message;
            }
            results[i] = result;
        }).ConfigureAwait(false);

        return results;
    }
}
