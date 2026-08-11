using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// Editor language features for a cell language: completion, hover, signature
/// help and diagnostics. Implemented in the language package so the LSP host
/// dispatches by cell languageId instead of hard-coding one branch per language.
/// <para>
/// Positions are expressed the way each feature already used them — character
/// offsets for completion/hover, line/column for diagnostics — so the host keeps
/// doing the offset↔position mapping it already does.
/// </para>
/// </summary>
public interface ICellLanguageServices {
    /// <summary>Completions at <paramref name="offset"/>, or null.</summary>
    Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context);

    /// <summary>Hover markdown at <paramref name="offset"/>, or null.</summary>
    Task<HoverResult> HoverAsync(string code, int offset);

    /// <summary>Signature help at <paramref name="offset"/>, or null.</summary>
    Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset);

    /// <summary>Syntax diagnostics for a whole document. Empty when none.</summary>
    IReadOnlyList<DiagnosticResult> Diagnose(string text);
}

/// <summary>
/// The editor-side context a language may fold into completion. The session-side
/// context (connection names, cube names, registered pipeline steps) comes from
/// the language's own session — the host does not know about those.
/// </summary>
public sealed class LanguageServiceContext {
    public LanguageServiceContext(IReadOnlyList<string> openDocuments = null) {
        OpenDocuments = openDocuments ?? Array.Empty<string>();
    }

    /// <summary>
    /// Text of every open cell of this language, so completion can offer things
    /// declared in sibling cells (e.g. <c>-- step</c> names in other SQL cells).
    /// </summary>
    public IReadOnlyList<string> OpenDocuments { get; }
}

/// <summary>Completion items plus the span they replace.</summary>
public sealed class CompletionResult {
    public int ReplaceStart { get; set; }
    public int ReplaceLength { get; set; }
    public List<CompletionEntry> Items { get; } = new List<CompletionEntry>();
}

/// <summary>One completion item. <see cref="Kind"/> is a language-neutral name
/// ("keyword", "function", "type", "operator", "connection", …) the host maps to
/// an LSP kind.</summary>
public sealed class CompletionEntry {
    public string Label { get; set; }
    public string InsertText { get; set; }
    public string Kind { get; set; }
    public string Detail { get; set; }
}

/// <summary>Hover markdown over a character span.</summary>
public sealed class HoverResult {
    public string Markdown { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
}

/// <summary>Signature help for the call being typed.</summary>
public sealed class SignatureHelpResult {
    public List<SignatureEntry> Signatures { get; } = new List<SignatureEntry>();
    public int ActiveSignature { get; set; }
    public int ActiveParameter { get; set; }
}

public sealed class SignatureEntry {
    public string Label { get; set; }
    public string Documentation { get; set; }
    public List<SignatureParameter> Parameters { get; } = new List<SignatureParameter>();
}

public sealed class SignatureParameter {
    public string Label { get; set; }
    public string Documentation { get; set; }
}

/// <summary>
/// A syntax diagnostic, in 1-based line/column as the underlying parsers report
/// it (the host converts to LSP's 0-based positions).
/// </summary>
public sealed class DiagnosticResult {
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public int Code { get; set; }
    public string Message { get; set; }
}
