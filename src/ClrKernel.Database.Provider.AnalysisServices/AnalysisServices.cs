using Azure.Core;
using ClrKernel.Database.Entra;

namespace ClrKernel.Database.Provider.AnalysisServices;
/// <summary>Refresh/process kinds (maps to the Tabular Object Model's RefreshType).</summary>
public enum SsasRefresh {
    Full,
    Calculate,
    DataOnly,
    Automatic,
    Add,
    Defragment,
    ClearValues,
}

/// <summary>
/// Entry point for Analysis Services (Tabular) from C# cells. Connect to
/// on-prem SSAS, Azure Analysis Services, or a Microsoft Fabric / Power BI
/// semantic model, then query with DAX, read metadata, and process the model.
/// <code>
/// var cube = AnalysisServices.Connect("DataWarehouseServer01.yourdomain.local", "AdventureWorksDW2025");
/// cube.Query("EVALUATE TOPN(100, 'Sales')");
/// cube.ProcessPartitions(new[] { ("Sales", "2026") });
/// </code>
/// <para>
/// Renamed from <c>Ssas</c> in 0.9 (D8); there is no alias, so an older notebook gets CS0103.
/// </para>
/// <para>
/// <b>This type's name matches the last segment of its own namespace</b>, which is fine for
/// every caller that exists — verified for <c>Language.Dax</c>, <c>ClrKernel.UnitTest</c>, this
/// namespace itself, and notebook cells (a script has no enclosing namespace). It is <b>not</b>
/// fine from a sibling under <c>ClrKernel.Database.Provider</c> — e.g. <c>…Provider.Fabric</c> —
/// where the simple name binds to the namespace and fails CS0234. A future provider needing this
/// type must qualify it as <c>Provider.AnalysisServices.AnalysisServices</c> or alias it with
/// <c>using</c>. See HANDOFF-17 §5, P7.
/// </para>
/// </summary>
public static class AnalysisServices {
    /// <summary>Connects to SSAS. With no user, uses Windows Integrated auth;
    /// with a user/password, uses basic auth.</summary>
    public static SsasConnection Connect(string server, string database, string user = null, string password = null) {
        var spec = new SsasConnectionSpec { Server = server, Database = database };
        if (!string.IsNullOrEmpty(user)) {
            spec.Auth = SsasAuthMode.UserPassword;
            spec.User = user;
            spec.Password = password;
        }
        return new SsasConnection(spec);
    }

    /// <summary>Connects using a raw ADOMD/AMO-style connection string.</summary>
    public static SsasConnection FromConnectionString(string connectionString) =>
        new SsasConnection(new SsasConnectionSpec {
            Auth = SsasAuthMode.ConnectionString,
            RawConnectionString = connectionString,
        });

    /// <summary>Connects to Azure Analysis Services with Microsoft Entra auth.</summary>
    public static SsasConnection ConnectAzureAnalysisServices(
        string server, string database, TokenCredential credential = null, string scope = null) {
        return AzureAd(server, database, credential, scope ?? EntraScopes.AzureAnalysisServices);
    }

    /// <summary>Connects to a Microsoft Fabric / Power BI semantic model via its
    /// XMLA endpoint with Microsoft Entra auth. <paramref name="workspace"/> is the
    /// workspace name and <paramref name="model"/> the dataset/semantic model name.</summary>
    public static SsasConnection ConnectFabric(
        string workspace, string model, TokenCredential credential = null) =>
        AzureAd(FabricServer(workspace), model, credential, EntraScopes.PowerBi);

    /// <summary>
    /// Connects to a Fabric / Power BI semantic model using the signed-in Windows identity
    /// (<c>Integrated Security=SSPI</c>) rather than a token this process fetches.
    /// </summary>
    /// <remarks>
    /// On a domain- or Entra-joined Windows machine, the XMLA endpoint accepts SSPI and Windows
    /// negotiates the Entra identity itself — no <c>az login</c>, no browser, no app registration.
    /// That is often the only path that works where conditional access governs sign-in, because a
    /// token this process fetches comes from a generic developer application the tenant may refuse.
    /// SSPI is Windows-only; elsewhere <see cref="ConnectFabric"/> is the one to use.
    /// </remarks>
    public static SsasConnection ConnectFabricIntegrated(string workspace, string model) =>
        Connect(FabricServer(workspace), model);

    /// <summary>The XMLA endpoint for a Fabric / Power BI workspace.</summary>
    public static string FabricServer(string workspace) =>
        "powerbi://api.powerbi.com/v1.0/myorg/" + workspace;

    private static SsasConnection AzureAd(string server, string database, TokenCredential credential, string scope) {
        var cred = credential ?? EntraAuth.DefaultWithInteractiveFallback();
        return new SsasConnection(new SsasConnectionSpec {
            Server = server,
            Database = database,
            Auth = SsasAuthMode.AzureAd,
            TokenProvider = () => EntraAuth.Token(cred, scope),
        });
    }

    internal static Microsoft.AnalysisServices.Tabular.RefreshType ToTabular(SsasRefresh refresh) => refresh switch {
        SsasRefresh.Full => Microsoft.AnalysisServices.Tabular.RefreshType.Full,
        SsasRefresh.Calculate => Microsoft.AnalysisServices.Tabular.RefreshType.Calculate,
        SsasRefresh.DataOnly => Microsoft.AnalysisServices.Tabular.RefreshType.DataOnly,
        SsasRefresh.Automatic => Microsoft.AnalysisServices.Tabular.RefreshType.Automatic,
        SsasRefresh.Add => Microsoft.AnalysisServices.Tabular.RefreshType.Add,
        SsasRefresh.Defragment => Microsoft.AnalysisServices.Tabular.RefreshType.Defragment,
        SsasRefresh.ClearValues => Microsoft.AnalysisServices.Tabular.RefreshType.ClearValues,
        _ => Microsoft.AnalysisServices.Tabular.RefreshType.Full,
    };
}
