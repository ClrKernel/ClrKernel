using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Fabric;

/// <summary>The Fabric warehouse/lakehouse connection's self-description. Everything
/// is credential + runtime lookup — the SQL endpoint is discovered from the Fabric
/// API, so there is nothing to configure and no directive form; it is used from C#
/// cells (<c>Fabric.Connect().Workspace(…).Warehouse(…)</c>).</summary>
public static class FabricConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = "Fabric",
        DisplayName = "Microsoft Fabric",
        Description = "Fabric warehouse / lakehouse SQL endpoints via Entra sign-in " +
            "(Fabric.Connect / Interactive / ClientSecret); the server is discovered at run time.",
        Settings = new ConnectionSetting[] {
            new() { Name = "credential", DisplayName = "Entra credential", RuntimeOnly = true },
            new() { Name = "server", DisplayName = "SQL endpoint", RuntimeOnly = true,
                Description = "Looked up from the Fabric API per workspace — never configured." },
        },
    };
}
