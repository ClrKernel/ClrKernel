using System.Collections.Generic;

namespace ClrKernel.PowerShell;

/// <summary>
/// A completion query result: the range to replace and the candidate items.
/// Language-server-agnostic so the LSP layer can map it to protocol types.
/// </summary>
public sealed class PowerShellCompletion {
    /// <summary>Start offset (into the queried text) of the span the items replace.</summary>
    public int ReplaceStart { get; set; }

    /// <summary>Length of the replaced span.</summary>
    public int ReplaceLength { get; set; }

    public List<PowerShellCompletionItem> Items { get; } = new();
}

/// <summary>One completion candidate.</summary>
public sealed class PowerShellCompletionItem {
    /// <summary>Display label (PowerShell's ListItemText, e.g. <c>Get-Process</c>).</summary>
    public string Label { get; set; }

    /// <summary>Text inserted on accept (PowerShell's CompletionText).</summary>
    public string InsertText { get; set; }

    /// <summary>
    /// PowerShell's <c>CompletionResultType</c> name (Command, ParameterName,
    /// Variable, Property, Method, ProviderItem, …) — mapped to an LSP kind by
    /// the caller.
    /// </summary>
    public string Kind { get; set; }

    /// <summary>Tooltip text (PowerShell's ToolTip), if any.</summary>
    public string Detail { get; set; }
}
