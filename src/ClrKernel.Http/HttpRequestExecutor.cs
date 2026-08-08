using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Http;

/// <summary>
/// Sends a parsed <see cref="HttpRequestSpec"/> after resolving its
/// placeholders, and captures the full <see cref="HttpExchange"/> (request,
/// response, headers, body, timing). A single shared <see cref="HttpClient"/>
/// (with decompression, redirects, and a cookie jar) backs all requests so
/// connections and cookies are reused across cells in a session.
/// </summary>
public sealed class HttpRequestExecutor {
    private static readonly HttpClient _client = CreateClient();

    private readonly string _baseDirectory;

    public HttpRequestExecutor(string baseDirectory) {
        _baseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();
    }

    private static HttpClient CreateClient() {
        var handler = new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        return new HttpClient(handler);
    }

    public async Task<HttpExchange> SendAsync(
        HttpRequestSpec spec, HttpVariableResolver resolver, CancellationToken cancellationToken = default) {
        var exchange = new HttpExchange {
            Name = spec.Name,
            Label = spec.Label,
            RequestMethod = spec.Method,
        };

        string url = resolver.Resolve(spec.Url);
        exchange.RequestUrl = url;

        HttpRequestMessage request;
        try {
            request = BuildRequest(spec, resolver, url, exchange);
        } catch (Exception e) {
            exchange.Error = e.Message;
            return exchange;
        }

        var stopwatch = Stopwatch.StartNew();
        try {
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            stopwatch.Stop();
            exchange.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;

            exchange.StatusCode = (int)response.StatusCode;
            exchange.ReasonPhrase = response.ReasonPhrase;
            exchange.HttpVersion = "HTTP/" + response.Version;

            foreach (var header in response.Headers) {
                foreach (var value in header.Value) {
                    exchange.ResponseHeaders.Add(new HttpNameValue(header.Key, value));
                }
            }
            if (response.Content != null) {
                foreach (var header in response.Content.Headers) {
                    foreach (var value in header.Value) {
                        exchange.ResponseHeaders.Add(new HttpNameValue(header.Key, value));
                    }
                }
                exchange.ContentType = response.Content.Headers.ContentType?.ToString();

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                exchange.BodyBytes = bytes;
                exchange.ContentLength = bytes.LongLength;

                if (IsTextual(exchange.ContentType)) {
                    var charset = response.Content.Headers.ContentType?.CharSet;
                    exchange.BodyText = Decode(bytes, charset);
                }
            }
        } catch (OperationCanceledException) {
            stopwatch.Stop();
            exchange.Error = "Request cancelled or timed out after " + Math.Round(stopwatch.Elapsed.TotalSeconds, 1) + "s.";
        } catch (HttpRequestException e) {
            stopwatch.Stop();
            exchange.Error = e.Message;
        } finally {
            request.Dispose();
        }

        return exchange;
    }

    private HttpRequestMessage BuildRequest(
        HttpRequestSpec spec, HttpVariableResolver resolver, string url, HttpExchange exchange) {
        var request = new HttpRequestMessage(new HttpMethod(spec.Method), url);

        // Resolve the body first so a Content-Type header applies to it.
        var bodyText = ResolveBody(spec, resolver);
        if (bodyText != null) {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyText));
            exchange.RequestBody = bodyText;
        }

        foreach (var header in spec.Headers) {
            var name = header.Name;
            var value = resolver.Resolve(header.Value);
            exchange.RequestHeaders.Add(new HttpNameValue(name, value));

            if (IsContentHeader(name)) {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                // Replace so a spec Content-Type wins over any default.
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            } else {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private string ResolveBody(HttpRequestSpec spec, HttpVariableResolver resolver) {
        if (spec.BodyFromFile != null) {
            var path = Path.IsPathRooted(spec.BodyFromFile)
                ? spec.BodyFromFile
                : Path.Combine(_baseDirectory, spec.BodyFromFile);
            var content = File.ReadAllText(path);
            return spec.BodyFileInterpolate ? resolver.Resolve(content) : content;
        }
        return spec.Body != null ? resolver.Resolve(spec.Body) : null;
    }

    private static bool IsContentHeader(string name) {
        switch (name.ToLowerInvariant()) {
            case "content-type":
            case "content-length":
            case "content-encoding":
            case "content-language":
            case "content-location":
            case "content-disposition":
            case "content-range":
            case "content-md5":
            case "expires":
            case "last-modified":
                return true;
            default:
                return false;
        }
    }

    private static bool IsTextual(string contentType) {
        if (string.IsNullOrEmpty(contentType)) {
            return true; // no type declared: assume text so we still show something
        }
        var ct = contentType.ToLowerInvariant();
        return ct.StartsWith("text/", StringComparison.Ordinal)
            || ct.Contains("json")
            || ct.Contains("xml")
            || ct.Contains("javascript")
            || ct.Contains("x-www-form-urlencoded")
            || ct.Contains("csv");
    }

    private static string Decode(byte[] bytes, string charset) {
        if (!string.IsNullOrEmpty(charset)) {
            try {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            } catch (ArgumentException) {
                // fall through to UTF-8
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
