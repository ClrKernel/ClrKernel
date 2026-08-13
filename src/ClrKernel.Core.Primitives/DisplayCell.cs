using System;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// A handle to one displayed output that can be re-rendered in place. This is a
/// structure, never a render: the engine suppresses a trailing <see cref="DisplayCell"/>
/// instead of formatting it. Updating a cell only raises the <see cref="DisplayValues"/>
/// events — the single display channel. Hosts listen, bundle the concept for their
/// wire format, and route by <see cref="DisplayId"/>, which is also how updates from
/// background work reach the originating cell's output after the cell has finished.
/// </summary>
public record DisplayCell(string DisplayId) {
    private bool _isDisplayed;

    public IDisplayValue Value { get; private set; }

    public DisplayCell Update(object value, Type preferredDisplayType = null, string preferredMimeType = null) {
        Value = value is IDisplayValue concept
            ? concept
            : new DisplayObject(value, preferredDisplayType, preferredMimeType);
        var isUpdate = _isDisplayed;
        _isDisplayed = true;
        DisplayValues.Notify(this, isUpdate);
        return this;
    }
}
