namespace ClrKernel.Sql;
/// <summary>
/// How a <see cref="SqlConnectionSpec"/> authenticates. Chosen so the same
/// notebook works across platforms: <see cref="Integrated"/> uses Windows
/// Integrated auth on Windows and Microsoft Entra (Azure AD) on macOS/Linux,
/// where classic Windows integrated auth is not available.
/// </summary>
public enum SqlAuthMode {
    /// <summary>SQL Server login: User ID + password resolved from the secret store.</summary>
    SqlPassword,

    /// <summary>Windows Integrated auth on Windows; Entra "Default" elsewhere.</summary>
    Integrated,

    /// <summary>Microsoft Entra "Default" (managed identity / az login / VS, etc.).</summary>
    AzureAdDefault,

    /// <summary>Microsoft Entra username + password (password from the secret store).</summary>
    AzureAdPassword,

    /// <summary>Microsoft Entra interactive (browser) sign-in.</summary>
    AzureAdInteractive,

    /// <summary>Use the supplied raw connection string as-is (it carries its own
    /// auth). The escape hatch for advanced/custom connection strings.</summary>
    RawConnectionString,
}
