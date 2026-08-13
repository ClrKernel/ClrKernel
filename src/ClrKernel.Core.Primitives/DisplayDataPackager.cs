using System;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// Packages a display concept into the <see cref="DisplayData"/> MIME bundle the
/// transports publish. This decides which MIME types to ask the
/// <see cref="DisplayFormatters"/> registry for — it renders nothing itself.
/// </summary>
public static class DisplayDataPackager {
    public static DisplayData Pack(IDisplayValue value) {
        var data = new DisplayData();
        value = DisplayFormatters.Resolve(value);

        // An explicit MIME preference publishes the raw value verbatim under that type
        // (the historical Display("...", mime) / DisplayAs behaviour).
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
        data.Data["text/plain"] = DisplayFormatters.TryFormat<DisplayText>(value, out var text)
            ? text.Text ?? ""
            : value.Value?.ToString() ?? "";
        return data;
    }
}
