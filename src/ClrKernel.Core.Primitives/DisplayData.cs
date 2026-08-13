using System.Collections.Generic;

namespace ClrKernel.Core.Primitives;
/// <summary>
/// This type of message is used to bring back data that
/// should be displayed (text, html, svg, etc.) in the frontends.
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#display-data
/// </summary>
/// <remarks>
/// A plain container (no serialization attributes): the transport layer
/// owns JSON. The keys of <see cref="Data"/> are MIME types (e.g.
/// "text/plain", "text/html") and the property names serialize to
/// data/metadata/transient via the transport's camelCase naming policy.
/// </remarks>
public class DisplayData {
    public Dictionary<string, object> Data { get; set; }

    public Dictionary<string, object> Metadata { get; set; }

    public Dictionary<string, object> Transient { get; set; }

    public DisplayData() {
        Data = new Dictionary<string, object>();
        Metadata = new Dictionary<string, object>();
        Transient = new Dictionary<string, object>();
    }

    public DisplayData(string text)
        : this(text, text) {

    }

    public DisplayData(string text, string html)
        : this() {
        Data = new Dictionary<string, object>
        {
            { "text/plain", text },
            { "text/html", html }
        };
    }
}
