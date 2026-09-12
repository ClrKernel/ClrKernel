using System;
using System.Collections.Concurrent;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// A hand-off shelf for values crossing into the C# script state: the engine puts
/// a value here under a one-time key and runs <c>var x = (T)SharedValues.Take("key")</c>
/// as a submission, which is the only way a live object gets a name in a Roslyn
/// script. Public because the script calls it; not for cells to use directly.
/// </summary>
public static class SharedValues {
    private static readonly ConcurrentDictionary<string, object> _values = new();

    public static string Put(object value) {
        var key = Guid.NewGuid().ToString("N");
        _values[key] = value;
        return key;
    }

    public static object Take(string key) =>
        _values.TryRemove(key, out var value)
            ? value
            : throw new InvalidOperationException("Shared value already taken or never put: " + key);
}
