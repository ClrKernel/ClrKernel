using System;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// Bundles a display concept into the <see cref="DisplayData"/> MIME bundle the
/// transports publish (the Jupyter display_data shape). This decides which MIME
/// types to ask the <see cref="DisplayFormatters"/> registry for — it renders
/// nothing itself. Hosts call it from their <see cref="DisplayValues"/> event
/// listeners; the engine calls it for trailing cell values.
/// </summary>
public static class MimeBundler {
    /// <summary>Bundles a cell's current value, stamping its display_id so the
    /// frontend can replace the output in place on updates.</summary>
    public static DisplayData Bundle(DisplayCell cell) {
        var data = Bundle(cell.Value);
        data.Transient["display_id"] = cell.DisplayId;
        return data;
    }

    public static DisplayData Bundle(IDisplayValue value) {
        var data = new DisplayData();
        value = DisplayFormatters.Resolve(value);

        // An explicit MIME preference publishes the raw value verbatim under that type.
        if (value is DisplayObject raw && !string.IsNullOrEmpty(raw.PreferredMimeType)) {
            data.Data[raw.PreferredMimeType] = raw.Value?.ToString() ?? "";
            return data;
        }

        if (value is DisplayBytes bytes) {
            var mime = string.IsNullOrEmpty(bytes.MimeType) ? "application/octet-stream" : bytes.MimeType;
            data.Data[mime] = Convert.ToBase64String(bytes.Bytes ?? Array.Empty<byte>());
            return data;
        }

        if (value is DisplayMarkdown markdown) {
            data.Data["text/markdown"] = markdown.Markdown ?? "";
        }
        if (DisplayFormatters.TryFormat<DisplayHtml>(value, out var html)) {
            data.Data["text/html"] = html.Html ?? "";
        }
        if (DisplayFormatters.TryFormat<DisplayText>(value, out var text)) {
            data.Data["text/plain"] = text.Text ?? "";
        } else if (!(value is DisplayHtml)) {
            // ToString is a fair plain-text stand-in for anything except raw HTML,
            // where it would put markup into text/plain.
            data.Data["text/plain"] = value.Value?.ToString() ?? "";
        }
        return data;
    }
}
