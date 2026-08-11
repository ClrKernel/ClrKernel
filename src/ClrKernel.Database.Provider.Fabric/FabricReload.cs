using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Database.Provider.Fabric;

/// <summary>
/// One table/segment to reload: delete a segment of a warehouse table, then reload
/// it from a source query. Used with <see cref="FabricWarehouse.ReloadBatch"/>.
/// </summary>
public sealed class FabricReloadRequest {
    /// <summary>Target schema (default <c>dbo</c>).</summary>
    public string TableSchema { get; set; } = "dbo";
    /// <summary>Target table name (unqualified).</summary>
    public string TableName { get; set; }
    /// <summary>A friendly label for the segment (for progress/errors); defaults to the table name.</summary>
    public string SegmentName { get; set; }
    /// <summary>An explicit DELETE statement to clear the segment. Takes precedence over <see cref="SegmentFilter"/>.</summary>
    public string DeleteCommand { get; set; }
    /// <summary>A WHERE predicate used to build <c>DELETE FROM target WHERE ...</c> when no <see cref="DeleteCommand"/> is set.</summary>
    public string SegmentFilter { get; set; }
    /// <summary>The source query text (informational; the source reader is produced by the caller's factory).</summary>
    public string SourceQuery { get; set; }
    /// <summary>Create the target table from the source schema if it doesn't exist.</summary>
    public bool CreateIfMissing { get; set; }

    internal string Target =>
        string.IsNullOrWhiteSpace(TableSchema) ? TableName : $"{TableSchema}.{TableName}";

    internal string Label => string.IsNullOrWhiteSpace(SegmentName) ? Target : SegmentName;

    internal string EffectiveDelete() {
        if (!string.IsNullOrWhiteSpace(DeleteCommand)) {
            return DeleteCommand;
        }

        if (!string.IsNullOrWhiteSpace(SegmentFilter)) {
            return $"DELETE FROM {WarehouseTableDefinition.QuoteTable(Target)} WHERE {SegmentFilter}";
        }
        return null; // full reload with no delete
    }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(TableName)) {
            throw new InvalidOperationException("FabricReloadRequest.TableName is required.");
        }
    }
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
    /// Reloads a batch of table segments in parallel: for each request, deletes the
    /// segment (via <c>DeleteCommand</c> or <c>SegmentFilter</c>) and reloads it from
    /// the reader returned by <paramref name="source"/>. Each request runs on its own
    /// connection, so up to <paramref name="maxParallelism"/> run concurrently.
    /// </summary>
    /// <param name="requests">Segments to reload.</param>
    /// <param name="source">Factory producing a fresh <see cref="IDataReader"/> for a request's source query.</param>
    /// <param name="maxParallelism">Maximum concurrent reloads (default 4).</param>
    /// <param name="stagingLakehouse">Staging lakehouse override; defaults to the one set by <see cref="WithStaging(string)"/>.</param>
    public IReadOnlyList<FabricReloadResult> ReloadBatch(
        IEnumerable<FabricReloadRequest> requests, Func<FabricReloadRequest, IDataReader> source,
        int maxParallelism = 4, string stagingLakehouse = null) =>
        ReloadBatchAsync(requests, source, maxParallelism, stagingLakehouse).GetAwaiter().GetResult();

    /// <inheritdoc cref="ReloadBatch"/>
    public async Task<IReadOnlyList<FabricReloadResult>> ReloadBatchAsync(
        IEnumerable<FabricReloadRequest> requests, Func<FabricReloadRequest, IDataReader> source,
        int maxParallelism = 4, string stagingLakehouse = null, CancellationToken cancellationToken = default) {
        if (requests is null) {
            throw new ArgumentNullException(nameof(requests));
        }

        if (source is null) {
            throw new ArgumentNullException(nameof(source));
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
                var delete = req.EffectiveDelete();
                if (delete != null) {
                    result.RowsDeleted = Execute(delete);
                }
                using var reader = source(req)
                    ?? throw new InvalidOperationException($"Source reader for segment '{req.Label}' was null.");
                var inserted = await BulkInsertAsync(
                    reader, req.Target, req.CreateIfMissing, stagingLakehouse, ct).ConfigureAwait(false);
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
