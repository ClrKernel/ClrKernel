using Newtonsoft.Json;

namespace ClrKernel.Protocols;

public class LanguageInfo {
    [JsonProperty("file_extension")]
    public string FileExtension { get; set; }
    [JsonProperty("mimetype")]
    public string MimeType { get; set; }
    [JsonProperty("name")]
    public string Name { get; set; }
    [JsonProperty("pygments_lexer")]
    public string PygmentsLexer { get; set; }
    [JsonProperty("version")]
    public string Version { get; set; }

    public LanguageInfo() {
        FileExtension = ".cs";
        MimeType = "text/x-csharp";
        Name = ".netstandard";
        PygmentsLexer = "CSharp";
        Version = typeof(string).Assembly.ImageRuntimeVersion.Substring(1);
    }
}
