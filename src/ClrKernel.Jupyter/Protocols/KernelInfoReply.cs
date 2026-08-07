using System.Text.Json.Serialization;

namespace ClrKernel.Jupyter.Protocols;
/// <summary>
/// https://jupyter-client.readthedocs.io/en/stable/messaging.html#kernel-info
/// </summary>
public class KernelInfoReply {
    [JsonPropertyName("protocol_version")]
    public string ProtocolVersion { get; set; }

    [JsonPropertyName("implementation")]
    public string Implementation { get; set; }

    [JsonPropertyName("implementation_version")]
    public string ImplementationVersion { get; set; }

    [JsonPropertyName("language_info")]
    public LanguageInfo LanguageInfo { get; set; }

    [JsonPropertyName("banner")]
    public string Banner { get; set; }

    public KernelInfoReply() {
        Implementation = "ClrKernel";
        ImplementationVersion = "0.1.0";
        ProtocolVersion = "5.3";
        LanguageInfo = new LanguageInfo();
    }
}
