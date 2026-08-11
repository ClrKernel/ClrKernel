using System;
using System.Collections.Concurrent;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// A process-local secret provider. Used as the write target in unit tests and
/// as an optional in-memory cache in front of a slower store (see
/// <see cref="SecretStore"/>). Secrets live only for the life of the process.
/// </summary>
public sealed class InMemorySecretProvider : ISecretProvider {
    private readonly ConcurrentDictionary<string, string> _secrets =
        new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    public string Name => "memory";
    public bool CanStore => true;

    public bool TryGet(string key, out string secret) => _secrets.TryGetValue(key, out secret);

    public void Set(string key, string secret) => _secrets[key] = secret ?? string.Empty;

    public void Delete(string key) => _secrets.TryRemove(key, out _);
}
