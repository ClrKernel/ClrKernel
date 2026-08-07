using System.Text.Json.Serialization;

namespace ClrKernel.Jupyter.Protocols;

public class Header {
    [JsonPropertyName("msg_id")]
    public string MessageId { get; set; }

    [JsonPropertyName("username")]
    public string UserName { get; set; }

    [JsonPropertyName("session")]
    public string Session { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }

    [JsonPropertyName("msg_type")]
    public string MessageType { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }
}
