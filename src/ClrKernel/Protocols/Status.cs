using System.Text.Json.Serialization;

namespace ClrKernel.Protocols;

public class Status {
    [JsonPropertyName("execution_state")]
    public string ExecutionState { get; set; }
}
