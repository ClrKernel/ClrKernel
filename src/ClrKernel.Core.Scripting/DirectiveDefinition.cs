using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Core.Scripting;

/// <summary>How a directive parameter consumes tokens.</summary>
public enum DirectiveParameterKind {
    /// <summary>Takes the next token as its value (<c>--server host1</c>).</summary>
    Value,
    /// <summary>Presence-only switch (<c>--default</c>).</summary>
    Flag,
    /// <summary>Takes the next token and splits it on the first <c>=</c> (<c>--option k=v</c>).</summary>
    KeyValue,
    /// <summary>Recognized but rejected with <see cref="DirectiveParameter.ForbiddenMessage"/> —
    /// how <c>--password</c> stays a good error instead of an "unknown flag".</summary>
    Forbidden,
}

/// <summary>
/// One flag of a <c>#!</c> directive: its canonical spelling, aliases, and how it
/// binds. This is the single source of truth a language declares once — the parser
/// (<see cref="DirectiveParser"/>), completions, diagnostics, and the RPC-served
/// language descriptor are all generated from it.
/// </summary>
public sealed class DirectiveParameter {
    /// <summary>Canonical flag spelling, e.g. <c>--server</c>.</summary>
    public string Name { get; init; }

    /// <summary>Alternate spellings (<c>--host</c>, <c>-s</c>). Matching is case-insensitive.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    public DirectiveParameterKind Kind { get; init; } = DirectiveParameterKind.Value;

    /// <summary>Parse fails when absent (or the bound value is blank) with
    /// "<c>{selector} requires {RequiredLabel ?? Name}.</c>".</summary>
    public bool Required { get; init; }

    /// <summary>Overrides <see cref="Name"/> in the required-error text when the message
    /// carries a hint, e.g. <c>--on &lt;key[,key...]&gt;</c>.</summary>
    public string RequiredLabel { get; init; }

    /// <summary>The flag meaningfully appears more than once (<c>--option a=1 --option b=2</c>).
    /// Metadata for completion and UI generation — the binder accumulates repeats either way,
    /// with last-one-wins on single lookups.</summary>
    public bool Repeatable { get; init; }

    /// <summary>Well-known values (auth modes, transports). Metadata for completion and
    /// UI generation — the parser does not enforce it; value validation with its exact
    /// error message stays in the language's post-bind code.</summary>
    public IReadOnlyList<string> EnumValues { get; init; }

    /// <summary>Shown as completion detail and in generated UI.</summary>
    public string Description { get; init; }

    /// <summary>The exact rejection text for <see cref="DirectiveParameterKind.Forbidden"/>.</summary>
    public string ForbiddenMessage { get; init; }

    /// <summary>The value shape named in a malformed-<see cref="DirectiveParameterKind.KeyValue"/>
    /// error, e.g. <c>source=dest</c> for <c>--map</c>.</summary>
    public string KeyValueHint { get; init; } = "key=value";

    /// <summary>True when <paramref name="token"/> is this parameter's name or an alias.</summary>
    public bool Matches(string token) =>
        string.Equals(Name, token, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A <c>#!</c> directive's full shape: selector plus every parameter it accepts.</summary>
public sealed class DirectiveDefinition {
    /// <summary>The selector this directive answers to, e.g. <c>#!sql-connect</c>.</summary>
    public string Selector { get; init; }

    public string Description { get; init; }

    public IReadOnlyList<DirectiveParameter> Parameters { get; init; } = Array.Empty<DirectiveParameter>();

    /// <summary>The parameter matching <paramref name="token"/>, or null.</summary>
    public DirectiveParameter Find(string token) => Parameters.FirstOrDefault(p => p.Matches(token));
}
