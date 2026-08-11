using Azure.Core;
using Azure.Identity;

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
/// var cube = Ssas.Connect("DataWarehouseServer01.yourdomain.local", "AdventureWorksDW2025");
/// cube.Query("EVALUATE TOPN(100, 'Sales')");
/// cube.ProcessPartitions(new[] { ("Sales", "2026") });
/// </code>
/// </summary>
public static class Ssas {
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
        return AzureAd(server, database, credential,
            scope ?? "https://*.asazure.windows.net/.default");
    }

    /// <summary>Connects to a Microsoft Fabric / Power BI semantic model via its
    /// XMLA endpoint with Microsoft Entra auth. <paramref name="workspace"/> is the
    /// workspace name and <paramref name="model"/> the dataset/semantic model name.</summary>
    public static SsasConnection ConnectFabric(
        string workspace, string model, TokenCredential credential = null) {
        var server = "powerbi://api.powerbi.com/v1.0/myorg/" + workspace;
        return AzureAd(server, model, credential,
            "https://analysis.windows.net/powerbi/api/.default");
    }

    private static SsasConnection AzureAd(string server, string database, TokenCredential credential, string scope) {
        var cred = credential ?? new DefaultAzureCredential(includeInteractiveCredentials: true);
        var context = new TokenRequestContext(new[] { scope });
        return new SsasConnection(new SsasConnectionSpec {
            Server = server,
            Database = database,
            Auth = SsasAuthMode.AzureAd,
            TokenProvider = () => cred.GetToken(context, default),
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
