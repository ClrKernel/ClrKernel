using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// Who Windows says signed in: the account's SID (what is stored — it survives a
/// rename), its <c>DOMAIN\user</c> (what is shown), and the SIDs of its groups.
/// </summary>
public sealed record WindowsLogin(string Sid, string Login, IReadOnlyCollection<string> GroupSids);

/// <summary>What a Windows sign-in is for. One route serves all four.</summary>
public enum WindowsSignInMode {
    SignIn,
    /// <summary>First run: become the Server Admin of an empty server.</summary>
    Setup,
    /// <summary>Redeem an invite code with a Windows account instead of a passkey.</summary>
    Invite,
    /// <summary>Add this Windows account to the account already signed in.</summary>
    Link,
}

/// <summary>
/// Signing in with a Windows account, through Negotiate (Kerberos, or NTLM where
/// Kerberos is not set up).
///
/// <para>
/// The handshake itself is the route's: it needs <c>UseAuthentication</c> and a
/// 401 the browser answers, none of which this class can do or needs to. What
/// arrives here is a <see cref="WindowsLogin"/>, and everything from there is the
/// account half's — <see cref="AuthService"/> decides which account gets made. So
/// this class is testable on any OS; only the route and the name lookups are not.
/// </para>
/// <para>
/// Offered when this process is a Windows service unless told otherwise: that is
/// the shared-machine case, where everyone already has a Windows login and
/// passkeys would ask each of them for a device and TLS.
/// </para>
/// </summary>
public sealed class WindowsProvider : IAccountProvider {
    private readonly AuthService _accounts;
    private readonly JobsOptions _options;
    private readonly ILogger<WindowsProvider> _log;
    private readonly Func<string, (string Sid, string Name)?> _resolve;
    private readonly Lazy<IReadOnlyDictionary<string, UserRole>> _groupRoles;

    /// <param name="resolve">Account or group name → SID and canonical name, or
    /// null when Windows does not know it. Tests pass their own; the default asks
    /// Windows and knows nothing anywhere else.</param>
    public WindowsProvider(
        AuthService accounts, JobsOptions options, ILogger<WindowsProvider> log,
        Func<string, (string Sid, string Name)?> resolve = null) {
        _accounts = accounts;
        _options = options;
        _log = log;
        _resolve = resolve ?? ResolveWithWindows;
        _groupRoles = new Lazy<IReadOnlyDictionary<string, UserRole>>(ParseGroups);
    }

    public string Name => IdentityProviders.Windows;

    public string DisplayName => "Windows account";

    public bool IsConfigured => Enabled(_options);

    /// <summary>
    /// Asked before the host is built, because it decides whether Negotiate is
    /// registered at all — off Windows, or when not wanted, it never is.
    /// </summary>
    internal static bool Enabled(JobsOptions options) =>
        OperatingSystem.IsWindows() && (options.WindowsSignIn ?? WindowsServiceHelpers.IsWindowsService());

    /// <summary>Group SID → the role its members get on their first sign-in.</summary>
    public IReadOnlyDictionary<string, UserRole> GroupRoles => _groupRoles.Value;

    /// <summary>
    /// The SID and canonical <c>DOMAIN\user</c> for what an admin typed, or null.
    /// A SID typed as such is accepted too.
    /// </summary>
    public (string Sid, string Name)? Resolve(string account) =>
        string.IsNullOrWhiteSpace(account) ? null : _resolve(account.Trim());

    public async Task<AuthResult> CompleteAsync(
        WindowsSignInMode mode, WindowsLogin login, User current, string inviteCode) {
        var proven = new ProvenIdentity(Name, login.Sid, login.Login);
        var now = DateTime.UtcNow;
        var existing = await _accounts.Store.FindIdentityAsync(Name, login.Sid);

        switch (mode) {
            case WindowsSignInMode.Link:
                if (current == null) {
                    return AuthResult.Fail("Sign in first.");
                }
                if (existing != null) {
                    return existing.UserId == current.Id
                        ? AuthResult.Success(current)
                        : AuthResult.Fail($"{login.Login} already signs in to another account here.");
                }
                await _accounts.LinkIdentityAsync(current, proven, now);
                return AuthResult.Success(current);

            case WindowsSignInMode.Setup:
            case WindowsSignInMode.Invite:
                if (existing != null) {
                    return AuthResult.Fail($"{login.Login} already has an account here. Sign in instead.");
                }
                var provisioned = mode == WindowsSignInMode.Setup
                    ? await _accounts.ProvisionAsync(
                        RegistrationPurpose.Bootstrap, Guid.NewGuid(), AuthService.UserPart(login.Login), null, now)
                    : await _accounts.ProvisionAsync(
                        RegistrationPurpose.Invite, Guid.NewGuid(), null, inviteCode, now, login.Sid);
                if (provisioned.Ok) {
                    await _accounts.LinkIdentityAsync(provisioned.User, proven, now);
                }
                return provisioned;

            default:
                if (existing == null) {
                    return await _accounts.ProvisionFromDirectoryAsync(proven, login.GroupSids, GroupRoles, now);
                }
                var signedIn = await _accounts.SignInAsync(proven);
                if (signedIn.Ok) {
                    await _accounts.RecordIdentityUseAsync(proven, now);
                }
                return signedIn;
        }
    }

    /// <summary>
    /// What Negotiate produced, reduced to a <see cref="WindowsLogin"/>. Null when
    /// it carries no SID, which a Windows logon always does.
    /// </summary>
    public static WindowsLogin LoginFrom(ClaimsPrincipal principal) {
        var sid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value;
        var groups = principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToList();
        if (OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity windows) {
            sid ??= windows.User?.Value;
            // A loop, not a Select: the platform guard above does not reach into a lambda.
            foreach (var group in windows.Groups ?? new IdentityReferenceCollection()) {
                groups.Add(group.Value);
            }
        }
        var name = principal.Identity?.Name;
        return string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(name) || IsAnonymousOrGuest(sid)
            ? null
            : new WindowsLogin(sid, name, groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// A null-session (anonymous) logon, or a machine's or domain's built-in Guest.
    /// Negotiate will complete a handshake for either, and neither is a person: an
    /// identity row for one would let anyone who can send an empty NTLM token in.
    /// </summary>
    internal static bool IsAnonymousOrGuest(string sid) =>
        sid is "S-1-5-7"                                   // NT AUTHORITY\ANONYMOUS LOGON
        || (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            && (sid.EndsWith("-501", StringComparison.Ordinal)     // Guest
                || sid.EndsWith("-514", StringComparison.Ordinal)));  // Domain Guests

    /// <summary>
    /// <c>CORP\Studio Users=ServerUser;CORP\Studio Admins=ServerAdmin</c>, each name
    /// resolved to a SID once. A name Windows does not know, or a role that is not
    /// one, is logged and skipped — loudly, because the failure is otherwise silent:
    /// a misspelt group provisions nobody and says nothing.
    /// </summary>
    private IReadOnlyDictionary<string, UserRole> ParseGroups() {
        var roles = new Dictionary<string, UserRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (_options.WindowsGroups ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var equals = entry.LastIndexOf('=');
            if (equals <= 0 || !Enum.TryParse<UserRole>(entry[(equals + 1)..].Trim(), out var role)) {
                _log.LogWarning(
                    "--windows-groups: '{Entry}' is not group=ServerAdmin|ServerViewer|ServerUser; ignored.", entry);
                continue;
            }
            var group = entry[..equals].Trim();
            if (Resolve(group) is not { } resolved) {
                _log.LogWarning("--windows-groups: Windows does not know a group called '{Group}'; ignored.", group);
                continue;
            }
            if (!roles.TryGetValue(resolved.Sid, out var already) || AuthService.Rank(role) > AuthService.Rank(already)) {
                roles[resolved.Sid] = role;
            }
        }
        return roles;
    }

    private static (string Sid, string Name)? ResolveWithWindows(string account) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }
        try {
            var sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(account)
                : (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            return (sid.Value, ((NTAccount)sid.Translate(typeof(NTAccount))).Value);
        } catch (SystemException) {
            // IdentityNotMappedException, a malformed SID (ArgumentException), or a
            // domain controller that cannot be reached — all of them "not known".
            return null;
        }
    }
}
