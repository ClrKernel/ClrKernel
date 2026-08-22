using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// Signs a test client in, as a real session against the real middleware.
/// <para>
/// Only the passkey ceremony is skipped — that needs a browser and an
/// authenticator, and it is covered end-to-end by the webapp's own suite. What
/// these tests are actually about is what a role may and may not do once signed
/// in, so the seam is deliberately just below the ceremony and above everything
/// else: a genuine user row, a genuine session, a genuine cookie.
/// </para>
/// </summary>
internal static class TestAuth {
    /// <summary>An auth store over the same SQLite file a test's run store uses.</summary>
    public static IAuthStore StoreFor(string dbPath) {
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        return new EfAuthStore(() => new SqliteRunsDbContext(options));
    }

    /// <summary>Creates a user at the given role and puts their session cookie on the client.</summary>
    public static async Task<User> SignInAsync(
        WebApplication app, HttpClient client, UserRole role, string displayName = null) {
        var store = app.Services.GetRequiredService<IAuthStore>();
        var auth = app.Services.GetRequiredService<AuthService>();
        var user = await store.CreateUserAsync(displayName ?? role.ToString(), role);
        var token = await auth.IssueSessionAsync(user.Id);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthService.CookieName}={token}");
        return user;
    }
}
