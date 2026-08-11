using System.Collections.Generic;

namespace ClrKernel.Language.Http;

/// <summary>
/// A single parsed request from a <c>.http</c> document (one of possibly many in
/// a cell, separated by <c>###</c>). Values still contain <c>{{…}}</c>
/// placeholders — they are resolved against the session's variables at send
/// time, not at parse time, so request chaining and system variables work.
/// </summary>
public sealed class HttpRequestSpec {
    /// <summary>
    /// The request's name if declared with <c>// @name foo</c> (or <c># @name foo</c>).
    /// Named requests can be referenced later as <c>{{foo.response.body.$.token}}</c>.
    /// </summary>
    public string Name { get; set; }

    /// <summary>HTTP method (GET, POST, …). Defaults to GET when the request line is a bare URL.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Request target — an absolute URL, still possibly holding <c>{{var}}</c> placeholders.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HTTP version token (e.g. "HTTP/1.1") if present; informational only.</summary>
    public string Version { get; set; }

    /// <summary>Header lines in declaration order (name + raw, unresolved value).</summary>
    public List<HttpHeaderLine> Headers { get; } = new();

    /// <summary>
    /// Raw request body (unresolved). Null when the request has no body. When the
    /// body was declared with <c>&lt; file</c> / <c>&lt;@ file</c>, this holds the
    /// directive and <see cref="BodyFromFile"/> is set.
    /// </summary>
    public string Body { get; set; }

    /// <summary>Path referenced by a <c>&lt; file</c> / <c>&lt;@ file</c> body directive, if any.</summary>
    public string BodyFromFile { get; set; }

    /// <summary>Whether a <c>&lt;@ file</c> body directive requested variable substitution in the file.</summary>
    public bool BodyFileInterpolate { get; set; }

    /// <summary>An optional label from the request's <c>###</c> separator line, for display.</summary>
    public string Label { get; set; }
}

/// <summary>A single header line: name and its still-unresolved value.</summary>
public sealed class HttpHeaderLine {
    public HttpHeaderLine(string name, string value) {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}
