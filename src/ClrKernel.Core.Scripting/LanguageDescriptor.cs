using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// The wire shape of one cell language — everything a front end (VS Code
/// extension, Jobs web UI, headless runner) needs to treat the language
/// generically: routing selectors, fence tags for markdown notebooks, directive
/// tables for completion/validation, and connection capability. Served by both
/// RPC surfaces (<c>serve</c> initialize, <c>clrkernel/languages</c>) so no
/// front end hard-codes a language list again.
/// </summary>
public sealed class LanguageDescriptor {
    public string Id { get; init; }

    public string DisplayName { get; init; }

    /// <summary>The selector serializers prepend to a bare cell (the language's
    /// first registered selector).</summary>
    public string DefaultSelector { get; init; }

    public IReadOnlyList<string> Selectors { get; init; } = Array.Empty<string>();

    /// <summary>Fenced-code-block tags this language claims in .nb.md / .dib documents.</summary>
    public IReadOnlyList<string> LanguageTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DirectiveDefinition> Directives { get; init; } = Array.Empty<DirectiveDefinition>();

    /// <summary>The language has named connections (an editor may offer a connection UI).</summary>
    public bool HasConnections { get; init; }

    /// <summary>Its connections can be loaded from / saved to connections.json.</summary>
    public bool ConfigBacked { get; init; }

    public static LanguageDescriptor From(ICellLanguage language) => new() {
        Id = language.Id,
        DisplayName = language.DisplayName,
        DefaultSelector = language.Selectors?.Count > 0 ? language.Selectors[0] : null,
        Selectors = language.Selectors ?? Array.Empty<string>(),
        LanguageTags = language.LanguageTags,
        Directives = language.Directives,
        HasConnections = language.Connections != null,
        ConfigBacked = language.Connections is IConfigBackedConnections,
    };

    /// <summary>The selector to emit for a fence tag: the tag's own selector when the
    /// language registers one (<c>zsh</c> → <c>#!zsh</c>), else the default.</summary>
    public string SelectorForTag(string tag) =>
        Selectors.FirstOrDefault(s => string.Equals(s, "#!" + tag, StringComparison.OrdinalIgnoreCase))
            ?? DefaultSelector;
}
