using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClrKernel.Protocols;
/// <summary>
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#execution-results
/// </summary>
public class ExecuteReplyOk : ExecuteReply {
    public ExecuteReplyOk() {
        Status = Protocols.StatusType.Ok;
    }

    [JsonPropertyName("payload")]
    public List<Dictionary<string, string>> Payload { get; set; }

    [JsonPropertyName("user_expressions")]
    public Dictionary<string, string> UserExpressions { get; set; }
}
