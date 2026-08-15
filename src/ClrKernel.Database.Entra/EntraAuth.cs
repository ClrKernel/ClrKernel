using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace ClrKernel.Database.Entra;

/// <summary>
/// The Microsoft Entra scopes ClrKernel's providers request. Centralised because a
/// wrong scope fails as an opaque 401 at sign-in rather than as a compile error.
/// </summary>
public static class EntraScopes {
    /// <summary>Azure Analysis Services (the wildcard is resolved by the service).</summary>
    public const string AzureAnalysisServices = "https://*.asazure.windows.net/.default";

    /// <summary>Fabric / Power BI XMLA endpoints and semantic models.</summary>
    public const string PowerBi = "https://analysis.windows.net/powerbi/api/.default";

    /// <summary>Azure SQL / Fabric Warehouse SQL endpoints.</summary>
    public const string SqlDatabase = "https://database.windows.net/.default";
}

/// <summary>
/// Shared Microsoft Entra credential construction and token acquisition for the
/// providers that need it — <c>Database.Provider.AnalysisServices</c> and
/// <c>Database.Provider.Fabric</c>. Lives in its own package so the dependency-free
/// <c>ClrKernel.Database</c> core, and the providers that authenticate some other way
/// (SQL Server does Entra inside its connection string; Oracle/ODBC/JDBC not at all),
/// don't inherit <c>Azure.Identity</c>.
/// </summary>
public static class EntraAuth {
    /// <summary>
    /// <see cref="DefaultAzureCredential"/> with interactive credentials enabled —
    /// the chain Analysis Services uses for Azure AS and Fabric semantic models.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> merged with <see cref="DefaultThenInteractiveBrowser"/>.
    /// The two providers have always built different chains, and the probe order decides
    /// which identity a developer signs in as. Collapsing them is a behaviour change on a
    /// path that fails as a surprise sign-in prompt or a silently wrong identity, and that
    /// no offline test can execute. If they should be unified, do it as its own change with
    /// a live tenant to verify against — not as a side effect of extracting shared code.
    /// </remarks>
    public static TokenCredential DefaultWithInteractiveFallback() =>
        new DefaultAzureCredential(includeInteractiveCredentials: true);

    /// <summary>
    /// <see cref="DefaultAzureCredential"/> (non-interactive) chained ahead of an explicit
    /// <see cref="InteractiveBrowserCredential"/> — the chain Fabric uses.
    /// </summary>
    /// <remarks>See the note on <see cref="DefaultWithInteractiveFallback"/>: these two
    /// are intentionally distinct.</remarks>
    public static TokenCredential DefaultThenInteractiveBrowser() =>
        new ChainedTokenCredential(
            new DefaultAzureCredential(includeInteractiveCredentials: false),
            new InteractiveBrowserCredential());

    /// <summary>
    /// An explicit browser sign-in <b>only</b> — no credential chain. Where the chains
    /// silently reuse whatever identity the environment offers (an az CLI session, VS
    /// sign-in, managed identity), this always opens the browser so the user picks the
    /// account. For the developer who wants to choose, not be chosen for.
    /// </summary>
    public static TokenCredential InteractiveOnly() =>
        new InteractiveBrowserCredential();

    /// <summary>An Entra service principal (client secret) credential.</summary>
    public static TokenCredential ClientSecret(string tenantId, string clientId, string clientSecret) {
        if (string.IsNullOrWhiteSpace(tenantId)) {
            throw new ArgumentException("tenantId is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(clientId)) {
            throw new ArgumentException("clientId is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(clientSecret)) {
            throw new ArgumentException("clientSecret is required.", nameof(clientSecret));
        }

        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    /// <summary>Acquires an access token for <paramref name="scope"/>.</summary>
    /// <remarks><see cref="TokenCredential"/> implementations cache internally, so callers
    /// are free to call this per operation rather than holding a token.</remarks>
    public static AccessToken Token(TokenCredential credential, string scope, CancellationToken cancellationToken = default) =>
        Validated(credential, scope).GetToken(new TokenRequestContext(new[] { scope }), cancellationToken);

    /// <summary>Acquires an access token for <paramref name="scope"/>.</summary>
    public static async Task<AccessToken> TokenAsync(
        TokenCredential credential, string scope, CancellationToken cancellationToken = default) =>
        await Validated(credential, scope)
            .GetTokenAsync(new TokenRequestContext(new[] { scope }), cancellationToken)
            .ConfigureAwait(false);

    private static TokenCredential Validated(TokenCredential credential, string scope) {
        if (credential is null) {
            throw new ArgumentNullException(nameof(credential));
        }

        if (string.IsNullOrWhiteSpace(scope)) {
            throw new ArgumentException("scope is required.", nameof(scope));
        }

        return credential;
    }
}
