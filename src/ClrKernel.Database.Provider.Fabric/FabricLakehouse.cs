using System;
using Azure.Storage.Files.DataLake;

namespace ClrKernel.Database.Provider.Fabric;

/// <summary>
/// A lakehouse in the workspace, used as the staging area for bulk-insert: query
/// results are written as Parquet into its <c>Files</c> area, then loaded into a
/// warehouse table with <c>OPENROWSET</c>.
/// </summary>
public sealed class FabricLakehouse {
    internal FabricWorkspace Workspace { get; }
    public Guid Id { get; }
    public string Name { get; }

    internal FabricLakehouse(FabricWorkspace workspace, Guid id, string name) {
        Workspace = workspace;
        Id = id;
        Name = name;
    }

    // OneLake file path inside the lakehouse's Files area, e.g. "Staging-BulkInsert/x.parquet".
    internal string FilesPath(string relativePath) => $"{Id}.Lakehouse/Files/{Trim(relativePath)}";

    /// <summary>A DataLake file client for a path under this lakehouse's Files area.</summary>
    internal DataLakeFileClient FileClient(string relativePath) {
        var fs = Workspace.Connection.OneLake.GetFileSystemClient(Workspace.Id.ToString());
        return fs.GetFileClient(FilesPath(relativePath));
    }

    /// <summary>The OneLake https URL for the file, as used by <c>OPENROWSET(BULK '...')</c>.</summary>
    internal string OneLakeUrl(string relativePath) =>
        $"{FabricConnection.OneLakeDfs}/{Workspace.Id}/{FilesPath(relativePath)}";

    private static string Trim(string p) => (p ?? string.Empty).Replace('\\', '/').TrimStart('/');
}
