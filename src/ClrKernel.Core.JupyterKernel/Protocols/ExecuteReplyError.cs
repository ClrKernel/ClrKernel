using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;
/// <summary>
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#execution-results
/// </summary>
public class ExecuteReplyError : ExecuteReply {
    public ExecuteReplyError() {
        Status = Protocols.StatusType.Error;
    }

    [JsonPropertyName("ename")]
    public string EName { get; set; }

    [JsonPropertyName("evalue")]
    public string EValue { get; set; }

    [JsonPropertyName("traceback")]
    public List<string> Traceback { get; set; }
}
