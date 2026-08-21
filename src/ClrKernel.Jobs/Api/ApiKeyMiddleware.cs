using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ClrKernel.Jobs;

/// <summary>
/// Guards <c>/api/*</c> with a static key when one is configured (<c>--api-key</c>,
/// <c>CLRKERNEL_JOBS_APIKEY</c>, or settings.json). No key configured = open, which
/// is why the default bind is localhost only. The comparison is fixed-time.
/// </summary>
public sealed class ApiKeyMiddleware {
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expected;

    public ApiKeyMiddleware(RequestDelegate next, JobsOptions options) {
        _next = next;
        _expected = string.IsNullOrEmpty(options.ApiKey) ? null : Encoding.UTF8.GetBytes(options.ApiKey);
    }

    public async Task InvokeAsync(HttpContext context) {
        if (_expected == null || !context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/api/health")) {
            await _next(context);
            return;
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (provided.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), _expected)) {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = $"Missing or invalid {HeaderName} header." });
            return;
        }

        await _next(context);
    }
}
