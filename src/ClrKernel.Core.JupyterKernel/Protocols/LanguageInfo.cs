using System.Text.Json.Serialization;

namespace ClrKernel.Core.JupyterKernel.Protocols;

public class LanguageInfo {
    [JsonPropertyName("file_extension")]
    public string FileExtension { get; set; }
    [JsonPropertyName("mimetype")]
    public string MimeType { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("pygments_lexer")]
    public string PygmentsLexer { get; set; }
    [JsonPropertyName("version")]
    public string Version { get; set; }

    public LanguageInfo() {
        FileExtension = ".cs";
        MimeType = "text/x-csharp";
        Name = ".netstandard";
        PygmentsLexer = "CSharp";
        Version = typeof(string).Assembly.ImageRuntimeVersion.Substring(1);
    }
}
