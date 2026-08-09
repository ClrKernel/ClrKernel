using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Microsoft.Fabric.Api;

namespace ClrKernel.Fabric;

/// <summary>
/// Entry point for the Fabric warehouse helper. From a C# notebook cell:
/// <code>
/// var fabric = Fabric.Connect();                 // interactive / default Entra sign-in
/// var wh = fabric.Workspace("Analytics").Warehouse("Sales");
/// wh.BulkInsert(reader, "dbo.FactSales", createIfMissing: true);
/// </code>
/// All connections use Microsoft Entra (Azure AD); no passwords are handled here.
/// </summary>
public static class Fabric {
    /// <summary>
    /// Connects using an interactive/default Entra credential chain
    /// (<see cref="DefaultAzureCredential"/> plus interactive browser fallback),
    /// suitable for a developer running a notebook locally.
    /// </summary>
    public static FabricConnection Connect() {
        var cred = new ChainedTokenCredential(
            new DefaultAzureCredential(includeInteractiveCredentials: false),
            new InteractiveBrowserCredential());
        return WithCredential(cred);
    }

    /// <summary>Connects with an Entra service principal (client secret).</summary>
    public static FabricConnection ClientSecret(string tenantId, string clientId, string clientSecret) {
        if (string.IsNullOrWhiteSpace(tenantId)) {
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(clientId)) {
            throw new ArgumentException("clientId is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(clientSecret)) {
            throw new ArgumentException("clientSecret is required.", nameof(clientSecret));
        }

        return WithCredential(new ClientSecretCredential(tenantId, clientId, clientSecret));
    }

    /// <summary>Connects with a caller-supplied Azure <see cref="TokenCredential"/>.</summary>
    public static FabricConnection WithCredential(TokenCredential credential) {
        if (credential is null) {
            throw new ArgumentNullException(nameof(credential));
        }

        return new FabricConnection(credential);
    }
}

/// <summary>A Fabric tenant connection: resolves workspaces and their warehouses.</summary>
public sealed class FabricConnection {
    internal const string OneLakeDfs = "https://onelake.dfs.fabric.microsoft.com";

    internal TokenCredential Credential { get; }
    internal FabricClient Client { get; }
    internal DataLakeServiceClient OneLake { get; }

    internal FabricConnection(TokenCredential credential) {
        Credential = credential;
        Client = new FabricClient(credential);
        OneLake = new DataLakeServiceClient(new Uri(OneLakeDfs), credential);
    }

    /// <summary>Resolves a workspace by display name (case-insensitive).</summary>
    public FabricWorkspace Workspace(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Workspace name is required.", nameof(name));
        }

        var match = Client.Core.Workspaces.ListWorkspaces()
            .FirstOrDefault(w => string.Equals(w.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) {
            throw new InvalidOperationException(
                $"Workspace '{name}' was not found (or you lack access). Available: " +
                string.Join(", ", Client.Core.Workspaces.ListWorkspaces().Select(w => w.DisplayName)));
        }
        return new FabricWorkspace(this, match.Id, match.DisplayName);
    }

    /// <summary>Uses a workspace by its id (no lookup call).</summary>
    public FabricWorkspace Workspace(Guid id) => new FabricWorkspace(this, id, id.ToString());

    /// <summary>Lists the workspaces the credential can see, as (id, name) pairs.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Workspaces() =>
        Client.Core.Workspaces.ListWorkspaces().Select(w => (w.Id, w.DisplayName)).ToList();
}
