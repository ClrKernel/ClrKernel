using Newtonsoft.Json;

namespace ClrKernel.Protocols;
/// <summary>
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#kernel-shutdown
/// </summary>
public class ShutdownRequest {
    [JsonProperty("restart")]
    public bool Restart { get; set; }
}

public class ShutdownReply {
    [JsonProperty("status")]
    public string Status { get; set; } = "ok";

    [JsonProperty("restart")]
    public bool Restart { get; set; }
}
