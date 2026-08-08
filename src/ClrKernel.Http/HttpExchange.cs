using System.Collections.Generic;

namespace ClrKernel.Http;

/// <summary>A name/value pair (header) preserving declaration/response order.</summary>
public sealed class HttpNameValue {
    public HttpNameValue(string name, string value) {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

/// <summary>
/// The outcome of sending one request: the resolved request, the response
/// (status, headers, body), and timing. Named exchanges are kept by the session
/// so later requests can reference <c>{{name.response.body.$.field}}</c>.
/// </summary>
public sealed class HttpExchange {
    public string Name { get; set; }
    public string Label { get; set; }

    // Resolved request (placeholders already substituted).
    public string RequestMethod { get; set; }
    public string RequestUrl { get; set; }
    public List<HttpNameValue> RequestHeaders { get; } = new();
    public string RequestBody { get; set; }

    // Response.
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; }
    public string HttpVersion { get; set; }
    public List<HttpNameValue> ResponseHeaders { get; } = new();
    public string ContentType { get; set; }
    public string BodyText { get; set; }
    public byte[] BodyBytes { get; set; }
    public long ContentLength { get; set; }
    public double ElapsedMs { get; set; }

    /// <summary>Set when the request could not be sent (DNS, connection, timeout, …). No response.</summary>
    public string Error { get; set; }

    public bool IsError => Error != null;
}
