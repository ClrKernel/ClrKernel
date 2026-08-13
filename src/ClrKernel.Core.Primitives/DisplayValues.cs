using System;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// The notebook-facing display API: each call creates a <see cref="DisplayCell"/> and
/// shows the value through the <see cref="DisplayFormatters"/> registry; the returned
/// cell re-renders in place via <c>Update*</c>. The <c>Display*</c> variants state which
/// concept the value should be treated as — never how that concept is rendered.
/// </summary>
public static class DisplayValues {
    public static event Action<DisplayCell> OnCellDisplayed;
    public static event Action<DisplayCell> OnCellUpdated;
    public static event Action<DisplayCell, Exception> OnCellDisplayError;

    internal static void Notify(DisplayCell cell, bool isUpdate) {
        try {
            if (isUpdate) {
                OnCellUpdated?.Invoke(cell);
            } else {
                OnCellDisplayed?.Invoke(cell);
            }
        } catch (Exception e) {
            OnCellDisplayError?.Invoke(cell, e);
        }
    }

    private static DisplayCell NewCell() => new DisplayCell(Guid.NewGuid().ToString("N"));

    public static DisplayCell Display(this object value, Type preferredDisplayType = null, string preferredMimeType = null) =>
        NewCell().Update(value, preferredDisplayType, preferredMimeType);

    public static DisplayCell DisplayTable(this object value) => NewCell().Update(value, typeof(DisplayTable));
    public static DisplayCell DisplayConsole(this object value) => NewCell().Update(value, typeof(DisplayConsoleText));
    public static DisplayCell DisplayHtml(this object value) => NewCell().Update(value, typeof(DisplayHtml));
    public static DisplayCell DisplayText(this object value) => NewCell().Update(value, typeof(DisplayText));
    public static DisplayCell DisplayMarkdown(this object value) => NewCell().Update(value, typeof(DisplayMarkdown));
    public static DisplayCell DisplayBytes(this object value, string mimeType) => NewCell().Update(value, typeof(DisplayBytes), mimeType);

    public static DisplayCell UpdateTable(this DisplayCell cell, object value) => cell.Update(value, typeof(DisplayTable));
    public static DisplayCell UpdateConsole(this DisplayCell cell, object value) => cell.Update(value, typeof(DisplayConsoleText));
    public static DisplayCell UpdateHtml(this DisplayCell cell, object value) => cell.Update(value, typeof(DisplayHtml));
    public static DisplayCell UpdateText(this DisplayCell cell, object value) => cell.Update(value, typeof(DisplayText));
    public static DisplayCell UpdateMarkdown(this DisplayCell cell, object value) => cell.Update(value, typeof(DisplayMarkdown));
    public static DisplayCell UpdateBytes(this DisplayCell cell, object value, string mimeType) => cell.Update(value, typeof(DisplayBytes), mimeType);
}
