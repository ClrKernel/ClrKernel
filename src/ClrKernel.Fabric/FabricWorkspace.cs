using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Fabric.Api.Core.Models;
using WarehouseItemsClient = Microsoft.Fabric.Api.Warehouse.ItemsClient;

namespace ClrKernel.Fabric;

/// <summary>A resolved Fabric workspace: a place to find warehouses and staging lakehouses.</summary>
public sealed class FabricWorkspace {
    internal FabricConnection Connection { get; }
    public Guid Id { get; }
    public string Name { get; }

    internal FabricWorkspace(FabricConnection connection, Guid id, string name) {
        Connection = connection;
        Id = id;
        Name = name;
    }

    /// <summary>Resolves a warehouse by display name and reads its SQL endpoint.</summary>
    public FabricWarehouse Warehouse(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Warehouse name is required.", nameof(name));
        }

        var item = ResolveItem(name, "Warehouse");
        var wc = new WarehouseItemsClient(Connection.Credential);
        var wh = wc.GetWarehouse(Id, item.Id!.Value).Value;
        var connectionString = wh.Properties?.ConnectionString
            ?? throw new InvalidOperationException($"Warehouse '{name}' did not report a SQL connection string.");
        return new FabricWarehouse(this, item.Id!.Value, item.DisplayName ?? name, connectionString);
    }

    /// <summary>References a staging lakehouse in this workspace by display name.</summary>
    public FabricLakehouse Lakehouse(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Lakehouse name is required.", nameof(name));
        }

        var item = ResolveItem(name, "Lakehouse");
        return new FabricLakehouse(this, item.Id!.Value, item.DisplayName ?? name);
    }

    /// <summary>Lists items of a given Fabric type (e.g. "Warehouse", "Lakehouse").</summary>
    public IReadOnlyList<(Guid Id, string Name)> Items(string type) =>
        Connection.Client.Core.Items.ListItems(Id, type: type)
            .Where(i => i.Id.HasValue)
            .Select(i => (i.Id!.Value, i.DisplayName ?? string.Empty))
            .ToList();

    private Item ResolveItem(string name, string type) {
        var items = Connection.Client.Core.Items.ListItems(Id, type: type).ToList();
        var match = items.FirstOrDefault(i =>
            string.Equals(i.DisplayName, name, StringComparison.OrdinalIgnoreCase) && i.Id.HasValue);
        if (match is null) {
            throw new InvalidOperationException(
                $"{type} '{name}' was not found in workspace '{Name}'. Available: " +
                string.Join(", ", items.Select(i => i.DisplayName)));
        }
        return match;
    }
}
