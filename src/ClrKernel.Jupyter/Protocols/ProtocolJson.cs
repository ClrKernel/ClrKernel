using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClrKernel.Jupyter.Protocols;

/// <summary>
/// Shared System.Text.Json configuration for the Jupyter wire protocol.
/// Property names default to camelCase (data, transient, ...); snake_case
/// fields (msg_id, execution_count, ...) are pinned with [JsonPropertyName].
/// Case-insensitive reads accept whatever casing a client sends. Relaxed
/// escaping keeps HTML/output payloads readable (no < for '<'), matching
/// the previous Newtonsoft behavior.
/// </summary>
public static class ProtocolJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes by the value's runtime type so derived message content
    /// (e.g. ExecuteReplyOk behind an ExecuteReply reference) is written in
    /// full — System.Text.Json otherwise serializes only the declared type's
    /// members. Null serializes to the JSON literal "null".
    /// </summary>
    public static string Serialize(object value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}
