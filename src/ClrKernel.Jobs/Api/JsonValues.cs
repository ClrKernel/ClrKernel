using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClrKernel.Jobs;

/// <summary>
/// Converts <see cref="JsonElement"/> values (what a JSON body binds to when the
/// target is <c>object</c>) into plain CLR values, so they can be written to YAML
/// and rendered as C# literals rather than serialized as their JsonElement shape.
/// </summary>
public static class JsonValues {
    public static Dictionary<string, object> ToPlain(Dictionary<string, object> values) =>
        values?.ToDictionary(kv => kv.Key, kv => ToPlain(kv.Value));

    public static object ToPlain(object value) => value switch {
        JsonElement element => FromElement(element),
        _ => value,
    };

    private static object FromElement(JsonElement element) => element.ValueKind switch {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => FromElement(p.Value)),
        _ => element.ToString(),
    };
}
