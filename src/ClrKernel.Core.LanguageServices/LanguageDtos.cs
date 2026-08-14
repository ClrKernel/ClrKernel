using System.Collections.Generic;

namespace ClrKernel.Core.LanguageServices;

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

/// <summary>
/// One place a symbol is defined in source. <see cref="InCurrentCell"/> means
/// <see cref="Start"/>/<see cref="Length"/> are offsets into the cell the query came
/// from (with <see cref="FullStart"/>/<see cref="FullLength"/> spanning the whole
/// declaration, so a peek frames the entire member); otherwise the definition sits in
/// a replayed (executed) submission, and <see cref="SourceLine"/> +
/// <see cref="ColumnInLine"/> let the host find the same line in whichever open cell
/// still contains it.
/// </summary>
public sealed record DefinitionLocationDto(
    bool InCurrentCell,
    int Start,
    int Length,
    string SourceLine,
    int ColumnInLine,
    int FullStart = -1,
    int FullLength = 0);

/// <summary>
/// Decompiled source for a metadata symbol (BCL, nuget, ClrKernel — anything
/// referenced without source). <see cref="Key"/> names the virtual document the host
/// serves it under; <see cref="Start"/>/<see cref="Length"/> select the member inside
/// <see cref="Text"/>.
/// </summary>
public sealed record MetadataSourceDto(string Key, string Text, int Start, int Length);

/// <summary>Definition lookup outcome: source locations, or decompiled metadata, or neither.</summary>
public sealed record DefinitionResultDto(
    IReadOnlyList<DefinitionLocationDto> Locations,
    MetadataSourceDto Metadata);
