using Newtonsoft.Json;

namespace ClrKernel.Protocols;

public class Status {
    [JsonProperty("execution_state")]
    public string ExecutionState { get; set; }
}
