using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClrKernel.Database.Provider.AnalysisServices;
/// <summary>How an <see cref="SsasConnection"/> authenticates.</summary>
public enum SsasAuthMode {
    /// <summary>Windows Integrated auth (SSPI) — the default for on-prem SSAS.</summary>
    Integrated,

    /// <summary>SSAS/basic username + password.</summary>
    UserPassword,

    /// <summary>Microsoft Entra (Azure AD) access token — Azure AS / Fabric / Power BI.</summary>
    AzureAd,

    /// <summary>Use the supplied raw connection string verbatim.</summary>
    ConnectionString,
}

/// <summary>
/// A secret-free description of an Analysis Services (Tabular) connection.
/// Produces the ADOMD connection string (for DAX/DMV queries) and the TOM
/// (AMO) connect string (for processing), applying the chosen auth. For
/// <see cref="SsasAuthMode.AzureAd"/> the token is supplied by
/// <see cref="TokenProvider"/> and set on the client objects rather than the
/// string. On non-Windows hosts, Integrated auth is not available — use
/// user/password or Entra.
/// </summary>
public sealed class SsasConnectionSpec {
    public string Server { get; set; }
    public string Database { get; set; }
    public SsasAuthMode Auth { get; set; } = SsasAuthMode.Integrated;
    public string User { get; set; }
    public string Password { get; set; }
    public string RawConnectionString { get; set; }
    public string Provider { get; set; } = "MSOLAP";

    /// <summary>For <see cref="SsasAuthMode.AzureAd"/>: returns a current access
    /// token for the Analysis Services / Power BI scope.</summary>
    public Func<Azure.Core.AccessToken> TokenProvider { get; set; }

    /// <summary>Connection string used by ADOMD.NET for DAX/DMV queries.</summary>
    public string BuildAdomdConnectionString() {
        if (Auth == SsasAuthMode.ConnectionString) {
            return RawConnectionString;
        }
        var sb = new StringBuilder();
        sb.Append("Provider=").Append(Provider).Append(';');
        sb.Append("Data Source=").Append(Server).Append(';');
        if (!string.IsNullOrWhiteSpace(Database)) {
            sb.Append("Catalog=").Append(Database).Append(';');
        }
        AppendAuth(sb);
        return sb.ToString();
    }

    /// <summary>Connect string used by the Tabular Object Model (AMO) server.</summary>
    public string BuildTomConnectionString() {
        if (Auth == SsasAuthMode.ConnectionString) {
            return RawConnectionString;
        }
        var sb = new StringBuilder();
        sb.Append("Data Source=").Append(Server).Append(';');
        if (!string.IsNullOrWhiteSpace(Database)) {
            sb.Append("Initial Catalog=").Append(Database).Append(';');
        }
        AppendAuth(sb);
        return sb.ToString();
    }

    private void AppendAuth(StringBuilder sb) {
        switch (Auth) {
            case SsasAuthMode.UserPassword:
                sb.Append("User ID=").Append(User ?? string.Empty).Append(';');
                sb.Append("Password=").Append(Password ?? string.Empty).Append(';');
                break;
            case SsasAuthMode.Integrated:
                // SSPI is Windows-only; leave it off elsewhere so the driver can
                // negotiate (or the user supplies a raw string / token).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    sb.Append("Integrated Security=SSPI;");
                }
                break;
            case SsasAuthMode.AzureAd:
                // Token is applied to the client object, not the string.
                break;
        }
    }

    /// <summary>A one-line description for status/logging (no secrets).</summary>
    public string Describe() {
        var auth = Auth switch {
            SsasAuthMode.Integrated => "Integrated",
            SsasAuthMode.UserPassword => "user/password" + (string.IsNullOrEmpty(User) ? "" : $" ({User})"),
            SsasAuthMode.AzureAd => "Entra",
            SsasAuthMode.ConnectionString => "custom connection string",
            _ => Auth.ToString(),
        };
        var target = string.IsNullOrEmpty(Database) ? Server : $"{Server}/{Database}";
        return $"{target} [{auth}]";
    }
}
