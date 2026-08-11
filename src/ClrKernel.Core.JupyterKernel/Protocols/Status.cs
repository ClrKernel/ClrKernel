using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;

public class Status {
    [JsonPropertyName("execution_state")]
    public string ExecutionState { get; set; }
}
