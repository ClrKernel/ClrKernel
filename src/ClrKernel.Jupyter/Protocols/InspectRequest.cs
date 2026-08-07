using System.Text.Json.Serialization;

namespace ClrKernel.Jupyter.Protocols;

/// <summary>
/// Jupyter inspect_request: introspection (hover-equivalent) for the token at
/// <see cref="CursorPos"/> in <see cref="Code"/>.
/// </summary>
public class InspectRequest {
    public string Code { get; set; }

    [JsonPropertyName("cursor_pos")]
    public int CursorPos { get; set; }

    [JsonPropertyName("detail_level")]
    public int DetailLevel { get; set; }
}
