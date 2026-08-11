using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;
/// <summary>
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#kernel-shutdown
/// </summary>
public class ShutdownRequest {
    [JsonPropertyName("restart")]
    public bool Restart { get; set; }
}

public class ShutdownReply {
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("restart")]
    public bool Restart { get; set; }
}
