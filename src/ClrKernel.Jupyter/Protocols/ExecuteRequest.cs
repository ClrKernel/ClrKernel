using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClrKernel.Jupyter.Protocols;

public class ExecuteRequest {
    public string Code { get; set; }

    public bool Silent { get; set; }

    [JsonPropertyName("store_history")]
    public bool StoreHistory { get; set; }

    [JsonPropertyName("user_expressions")]
    public Dictionary<string, object> UserExpressions { get; set; }

    [JsonPropertyName("allow_stdin")]
    public bool AllowStdin { get; set; }

    [JsonPropertyName("stop_on_error")]
    public bool StopOnError { get; set; }
}
