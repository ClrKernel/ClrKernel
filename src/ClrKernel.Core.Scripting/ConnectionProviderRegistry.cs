using System;
using System.Collections.Generic;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// The default set of connection-provider descriptors, set once by the
/// composition root (and the test fixtures) alongside
/// <see cref="CellLanguageRegistry.Default"/>. Each engine copies it into its
/// own mutable list, so a provider loaded mid-session (<c>#r "nuget: …"</c>)
/// registers with that session only — the same isolation rule as languages.
/// </summary>
public static class ConnectionProviderRegistry {
    public static IReadOnlyList<ConnectionProviderDescriptor> Default { get; set; }
        = Array.Empty<ConnectionProviderDescriptor>();
}
