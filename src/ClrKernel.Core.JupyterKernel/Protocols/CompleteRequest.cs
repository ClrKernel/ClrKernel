using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;

/// <summary>
/// Jupyter complete_request: tab-completion for <see cref="Code"/> at
/// <see cref="CursorPos"/> (a Unicode codepoint offset).
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#completion
/// </summary>
public class CompleteRequest {
    public string Code { get; set; }

    [JsonPropertyName("cursor_pos")]
    public int CursorPos { get; set; }
}
