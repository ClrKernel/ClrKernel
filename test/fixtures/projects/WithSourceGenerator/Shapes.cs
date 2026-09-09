using System.Text.Json.Serialization;

namespace WithSourceGenerator;

public record Point(int X, int Y);

// Shapes.Default.Point is generated at build time. A test adds a second
// [JsonSerializable] here and re-runs the #r, so the generated member it then
// asks for did not exist when the first build ran.
[JsonSerializable(typeof(Point))]
public partial class Shapes : JsonSerializerContext { }
