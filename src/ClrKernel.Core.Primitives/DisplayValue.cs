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

/// <summary>
/// Tabular data already shaped into columns and rows of display text.
/// <see cref="TotalRows"/> is the full row count before any limit; <c>-1</c> means the
/// source was truncated with the remainder uncounted ("first N+"); <c>null</c> means
/// nothing was truncated. The Kind constants and helpers define the shared column-kind
/// and cell-stringification conventions producers use to build the concept.
/// </summary>
public record DisplayTable(
    object Value,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> Types = null,
    int? TotalRows = null) : IDisplayValue {

    /// <summary>Column kinds renderers understand for sorting and stats.</summary>
    public const string Number = "number";
    public const string Date = "date";
    public const string Text = "string";

    public static string KindOf(Type type) {
        type = type == null ? null : Nullable.GetUnderlyingType(type) ?? type;
        if (type == null) {
            return Text;
        }
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal)) {
            return Number;
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) {
            return Date;
        }
        return Text;
    }

    public static string CellText(object value) {
        if (value == null || value is DBNull) {
            return null;
        }
        if (value is string s) {
            return s;
        }
        if (value is IFormattable formattable) {
            return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        }
        return value.ToString();
    }
}

/// <summary>
/// A one-line status: a short label pill followed by text — "fcst · 2 result sets ·
/// 12 ms", "MERGE dbo.Target · inserted 5". <see cref="Tone"/> hints the render
/// (<see cref="Success"/> or default/informational).
/// </summary>
public record DisplayBadge(string Label, string Text, string Tone = null) : IDisplayValue {
    public const string Success = "success";
    public object Value => Label + ": " + Text;
}

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

/// <summary>Binary content — images, pdfs, anything addressed by MIME type.</summary>
public record DisplayBytes(byte[] Bytes, string MimeType) : IDisplayValue {
    public object Value => Bytes;
}
