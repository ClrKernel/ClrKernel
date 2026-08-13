using System;
using System.Collections.Generic;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// A display concept: structured data describing <em>what</em> to show, never how to
/// render it. Rendering is a registered <see cref="DisplayFormatter"/> converting one
/// concept to another (see the ClrKernel.Formatting.* packages), so producers — cell
/// languages, data providers, user code — hand over concepts and stay renderer-agnostic.
/// </summary>
public interface IDisplayValue {
    object Value { get; }
}

/// <summary>
/// An arbitrary value awaiting formatting, optionally carrying the caller's stated
/// preference: a concept to convert to first (<see cref="PreferredDisplayType"/>) or a
/// raw MIME type to publish under verbatim (<see cref="PreferredMimeType"/>).
/// </summary>
public record DisplayObject(object Value, Type PreferredDisplayType = null, string PreferredMimeType = null) : IDisplayValue;

/// <summary>Tabular data already shaped into columns and rows of display text.</summary>
public record DisplayTable(
    object Value,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> Types = null,
    int? TotalRows = null) : IDisplayValue;

/// <summary>Console output, possibly containing ANSI escape sequences.</summary>
public record DisplayConsoleText(string ConsoleOutput) : IDisplayValue {
    public object Value => ConsoleOutput;
}

public record DisplayText(string Text) : IDisplayValue {
    public object Value => Text;
}

public record DisplayHtml(string Html) : IDisplayValue {
    public object Value => Html;
}

public record DisplayMarkdown(string Markdown) : IDisplayValue {
    public object Value => Markdown;
}

public record DisplayProgress(string Label, string Status, decimal Completed, decimal Total) : IDisplayValue {
    public object Value => this;
}

/// <summary>Binary content — images, pdfs, anything addressed by MIME type.</summary>
public record DisplayBytes(byte[] Bytes, string MimeType) : IDisplayValue {
    public object Value => Bytes;
}
