using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Data.Secrets;
/// <summary>
/// The secret resolver used by SQL connections. Composes an ordered chain of
/// <see cref="ISecretProvider"/>s and resolves a key against them in turn.
/// <para>
/// Default chain: an optional in-memory cache, the OS-native credential store
/// (Keychain / Credential Manager / libsecret), then environment variables.
/// Reads stop at the first hit and are cached; writes go to the first
/// store-capable provider (the OS store by default). Enterprise PAM providers
/// (Vault, Key Vault, CyberArk) can be inserted with <see cref="AddProvider"/>
/// without touching call sites.
/// </para>
/// Secret values are never written to notebooks or any committed file — only a
/// key name (the "secret ref") is ever persisted.
/// </summary>
public sealed class SecretStore {
    private readonly List<ISecretProvider> _providers = new List<ISecretProvider>();
    private readonly InMemorySecretProvider _cache;
    private readonly bool _useCache;

    public SecretStore(bool cacheLocally = true) {
        _useCache = cacheLocally;
        _cache = new InMemorySecretProvider();
        if (_useCache) {
            _providers.Add(_cache);
        }
        var os = OsSecretProvider.TryCreate();
        if (os != null) {
            _providers.Add(os);
        }
        _providers.Add(new EnvironmentSecretProvider());
    }

    /// <summary>A store backed only by the given providers (used by tests).</summary>
    public static SecretStore ForProviders(params ISecretProvider[] providers) {
        var store = new SecretStore(cacheLocally: false);
        store._providers.Clear();
        store._providers.AddRange(providers);
        return store;
    }

    /// <summary>Inserts a provider ahead of the environment fallback (e.g. a PAM).</summary>
    public void AddProvider(ISecretProvider provider) {
        if (provider == null) {
            throw new ArgumentNullException(nameof(provider));
        }
        var envIndex = _providers.FindIndex(p => p is EnvironmentSecretProvider);
        if (envIndex < 0) {
            _providers.Add(provider);
        } else {
            _providers.Insert(envIndex, provider);
        }
    }

    /// <summary>The provider names in resolution order (for diagnostics).</summary>
    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

    /// <summary>True when at least one provider can persist secrets.</summary>
    public bool CanStore => _providers.Any(p => p.CanStore);

    public bool TryResolve(string key, out string secret) {
        foreach (var provider in _providers) {
            if (provider.TryGet(key, out secret) && secret != null) {
                if (_useCache && !ReferenceEquals(provider, _cache)) {
                    _cache.Set(key, secret);
                }
                return true;
            }
        }
        secret = null;
        return false;
    }

    public string Resolve(string key) {
        if (TryResolve(key, out var secret)) {
            return secret;
        }
        throw new SecretNotFoundException(
            $"No secret found for '{key}'. Looked in: {string.Join(", ", ProviderNames)}. " +
            "Store one from the SQL connection panel, or set the " +
            $"{EnvironmentSecretProvider.EnvName(key)} environment variable.");
    }

    /// <summary>Stores a secret in the first store-capable provider and caches it.</summary>
    public string Store(string key, string secret) {
        var target = _providers.FirstOrDefault(p => p.CanStore && !ReferenceEquals(p, _cache))
            ?? _providers.FirstOrDefault(p => p.CanStore)
            ?? throw new InvalidOperationException("No writable secret provider is available.");
        target.Set(key, secret);
        if (_useCache) {
            _cache.Set(key, secret);
        }
        return target.Name;
    }

    public void Delete(string key) {
        foreach (var provider in _providers.Where(p => p.CanStore)) {
            try {
                provider.Delete(key);
            } catch {
                // best effort across providers
            }
        }
    }
}
