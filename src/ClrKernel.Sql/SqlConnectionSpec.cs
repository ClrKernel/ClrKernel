using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClrKernel.Data.Secrets;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Sql;
/// <summary>
/// A named, secret-free description of a SQL Server connection. Everything here
/// is safe to commit: passwords are never stored — only <see cref="SecretRef"/>,
/// the name of a key in the <see cref="SecretStore"/>. The live connection
/// string is composed on demand by <see cref="BuildConnectionString"/>, which
/// resolves the password at execution time.
/// </summary>
public sealed class SqlConnectionSpec {
    public string Name { get; set; }
    public string Server { get; set; }
    public string Database { get; set; }
    public SqlAuthMode Auth { get; set; } = SqlAuthMode.Integrated;
    public string User { get; set; }

    /// <summary>Key in the secret store holding the password. Defaults to
    /// <c>sql:&lt;name&gt;</c> when a password-based auth mode needs one.</summary>
    public string SecretRef { get; set; }

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }

    /// <summary>Advanced escape hatch: a raw connection string used as the base
    /// before structured fields and the resolved secret are applied.</summary>
    public string RawConnectionString { get; set; }

    /// <summary>Reserved for future engines; only "sqlserver" is supported now.</summary>
    public string Provider { get; set; } = "sqlserver";

    /// <summary>Extra connection-string keywords (e.g. Connect Timeout).</summary>
    public Dictionary<string, string> ExtraOptions { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The secret key actually used (explicit ref or the default).</summary>
    public string EffectiveSecretRef =>
        string.IsNullOrEmpty(SecretRef) ? "sql:" + Name : SecretRef;

    /// <summary>Whether this spec needs a password from the secret store.</summary>
    public bool NeedsSecret =>
        Auth == SqlAuthMode.SqlPassword || Auth == SqlAuthMode.AzureAdPassword;

    /// <summary>
    /// Builds the live connection string, resolving the password from the
    /// secret store when the auth mode requires it. The returned string is
    /// used immediately and never persisted.
    /// </summary>
    public string BuildConnectionString(SecretStore secrets) {
        if (secrets == null) {
            throw new ArgumentNullException(nameof(secrets));
        }

        var b = string.IsNullOrWhiteSpace(RawConnectionString)
            ? new SqlConnectionStringBuilder()
            : new SqlConnectionStringBuilder(RawConnectionString);

        // Raw mode: trust the supplied connection string (it carries its own auth
        // and options); apply only extra keywords.
        if (Auth == SqlAuthMode.RawConnectionString) {
            foreach (var kv in ExtraOptions) {
                b[kv.Key] = kv.Value;
            }
            return b.ConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(Server)) {
            b.DataSource = Server;
        }
        if (!string.IsNullOrWhiteSpace(Database)) {
            b.InitialCatalog = Database;
        }
        b.Encrypt = Encrypt;
        if (TrustServerCertificate) {
            b.TrustServerCertificate = true;
        }

        switch (Auth) {
            case SqlAuthMode.SqlPassword:
                b.UserID = User ?? string.Empty;
                b.Password = secrets.Resolve(EffectiveSecretRef);
                break;

            case SqlAuthMode.Integrated:
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    b.IntegratedSecurity = true;
                } else {
                    // Classic Windows integrated auth is Windows-only; fall back
                    // to Entra "Default" on macOS/Linux.
                    b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                }
                break;

            case SqlAuthMode.AzureAdDefault:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                if (!string.IsNullOrWhiteSpace(User)) {
                    b.UserID = User;
                }
                break;

            case SqlAuthMode.AzureAdPassword:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                b.UserID = User ?? string.Empty;
                b.Password = secrets.Resolve(EffectiveSecretRef);
                break;

            case SqlAuthMode.AzureAdInteractive:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(User)) {
                    b.UserID = User;
                }
                break;
        }

        foreach (var kv in ExtraOptions) {
            b[kv.Key] = kv.Value;
        }

        return b.ConnectionString;
    }

    /// <summary>A one-line description for UI/status (no secrets).</summary>
    public string Describe() {
        var auth = Auth switch {
            SqlAuthMode.SqlPassword => "SQL login" + (string.IsNullOrEmpty(User) ? "" : $" ({User})"),
            SqlAuthMode.Integrated => "Integrated",
            SqlAuthMode.AzureAdDefault => "Entra (default)",
            SqlAuthMode.AzureAdPassword => "Entra password" + (string.IsNullOrEmpty(User) ? "" : $" ({User})"),
            SqlAuthMode.AzureAdInteractive => "Entra interactive",
            SqlAuthMode.RawConnectionString => "custom connection string",
            _ => Auth.ToString(),
        };
        var target = string.IsNullOrEmpty(Database) ? Server : $"{Server}/{Database}";
        return $"{Name} → {target} [{auth}]";
    }
}
