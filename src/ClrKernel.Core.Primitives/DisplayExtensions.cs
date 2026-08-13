using System;
using System.Collections.Generic;
using System.Data;

namespace ClrKernel.Core.Primitives;
/// <summary>
/// Display helpers available in notebook cells via the ClrKernel.Core.Primitives
/// namespace import. Kept separate from <see cref="DisplayDataEmitter"/> (which
/// the kernel imports with <c>using static</c>) so extension-method resolution
/// stays unambiguous.
/// <para>
/// The <c>DisplayTable</c> overloads shape their source into the
/// <see cref="DisplayTable"/> concept — how that concept renders (the interactive
/// grid with sort, filter, and Analyze) is up to the registered formatters
/// (see ClrKernel.Formatting.Html).
/// </para>
/// </summary>
public static class DisplayExtensions {
    /// <summary>
    /// Displays content and returns a handle that can update it in place
    /// (e.g. <c>var dv = "".DisplayAs("text/html"); dv.Update(html);</c>).
    /// </summary>
    public static DisplayedValue DisplayAs(this string content, string mimeType) {
        var displayId = Guid.NewGuid().ToString("N");
        var data = new DisplayData();
        data.Data[mimeType] = content ?? "";
        data.Transient["display_id"] = displayId;
        DisplayDataEmitter.Emit(data);

        // Capture the current update handler: it is bound to the executing
        // cell's parent message, so later background updates still publish
        // against the right output even after the cell completes.
        var update = DisplayDataEmitter.UpdateDisplayDataHandler;
        return new DisplayedValue(displayId, mimeType, d => update?.Invoke(d));
    }

    // object.Display() lives in DisplayValues, routed through the
    // DisplayFormatters registry rather than ToString into one MIME type.

    /// <summary>
    /// Displays an ADO.NET data reader (e.g. a <c>Microsoft.Data.SqlClient</c>
    /// SQL Server query result) as tabular data. Column kinds come from the
    /// reader's schema, so numeric and date columns sort and summarize
    /// correctly. The reader is consumed (rows are read up to
    /// <paramref name="limit"/>, but remaining rows are still counted for the
    /// "showing first N of M" label).
    /// </summary>
    public static DisplayCell DisplayTable(this IDataReader reader, int limit = 1000) {
        if (reader == null) {
            throw new ArgumentNullException(nameof(reader));
        }
        return TableExtractor.Extract(reader, limit).Display();
    }

    /// <summary>Displays a <see cref="DataTable"/> as tabular data; column kinds
    /// come from <see cref="DataColumn.DataType"/>.</summary>
    public static DisplayCell DisplayTable(this DataTable table, int limit = 1000) {
        if (table == null) {
            throw new ArgumentNullException(nameof(table));
        }
        return TableExtractor.Extract(table, limit).Display();
    }

    /// <summary>Displays dictionary rows (column name → value, e.g. data-reader
    /// previews) as tabular data; columns are the union of keys in order of
    /// first appearance.</summary>
    public static DisplayCell DisplayTable(this IEnumerable<IDictionary<string, object>> rows, int limit = 1000) {
        return TableExtractor.Extract(rows, limit).Display();
    }

    /// <summary>
    /// Displays a sequence as tabular data (columns from the element type's
    /// public properties; scalar sequences get a single Value column). Mirrors
    /// .NET Interactive's DisplayTable().
    /// </summary>
    public static DisplayCell DisplayTable<T>(this IEnumerable<T> source, int limit = 1000) {
        return TableExtractor.Extract(source, limit).Display();
    }
}
