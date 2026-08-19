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

    /// <summary>True when the text already leads with one of this language's selectors
    /// as a whole token (e.g. a fence whose body is a <c>#!sql-connect</c> line).</summary>
    public bool StartsWithSelector(string text) {
        var lead = (text ?? string.Empty).TrimStart();
        return Selectors.Any(s =>
            lead.StartsWith(s, StringComparison.OrdinalIgnoreCase) &&
            (lead.Length == s.Length || char.IsWhiteSpace(lead[s.Length])));
    }

    /// <summary>An executable block for a fence of this language: the tag's selector is
    /// prepended so the engine routes it, unless the text is already selectored.</summary>
    public string BlockForTag(string tag, string text) {
        if (StartsWithSelector(text)) {
            return text;
        }
        var selector = SelectorForTag(tag);
        return selector == null ? text : selector + "\n" + text;
    }

    /// <summary>Fence-tag → descriptor lookup over a descriptor list (first claim wins).</summary>
    public static Dictionary<string, LanguageDescriptor> ByTag(IEnumerable<LanguageDescriptor> languages) {
        var byTag = new Dictionary<string, LanguageDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages ?? Array.Empty<LanguageDescriptor>()) {
            foreach (var tag in language.LanguageTags ?? Array.Empty<string>()) {
                byTag.TryAdd(tag, language);
            }
        }
        return byTag;
    }
}
