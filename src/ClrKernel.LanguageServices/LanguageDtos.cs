using System.Collections.Generic;

namespace ClrKernel.LanguageServices;

/// <summary>A single completion candidate, editor-neutral.</summary>
public sealed record CompletionItemDto(
    string Label,
    string InsertText,
    string SortText,
    string FilterText,
    string Kind,
    string Detail);

/// <summary>
/// A completion result: the items plus the span of existing text they replace
/// (relative to the queried cell code), so the editor swaps the partial word.
/// </summary>
public sealed record CompletionResultDto(
    int ReplaceStart,
    int ReplaceLength,
    IReadOnlyList<CompletionItemDto> Items);

/// <summary>Hover / quick-info: markdown plus the span it describes (cell-relative).</summary>
public sealed record HoverDto(
    string Markdown,
    int Start,
    int Length);

/// <summary>One parameter of a signature.</summary>
public sealed record ParameterDto(string Label, string Documentation);

/// <summary>One overload in a signature-help set.</summary>
public sealed record SignatureDto(
    string Label,
    string Documentation,
    IReadOnlyList<ParameterDto> Parameters);

/// <summary>Signature help: the overloads plus which one and which parameter are active.</summary>
public sealed record SignatureHelpDto(
    IReadOnlyList<SignatureDto> Signatures,
    int ActiveSignature,
    int ActiveParameter);
