using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;

/// <summary>Jupyter complete_reply: the matches and the text span they replace.</summary>
public class CompleteReply {
    public List<string> Matches { get; set; } = new();

    [JsonPropertyName("cursor_start")]
    public int CursorStart { get; set; }

    [JsonPropertyName("cursor_end")]
    public int CursorEnd { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = new();

    public string Status { get; set; } = "ok";
}
