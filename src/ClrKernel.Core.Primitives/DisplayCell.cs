using System;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// A handle to one displayed output that can be re-rendered in place. This is a
/// structure, never a render: the engine suppresses a trailing <see cref="DisplayCell"/>
/// instead of formatting it. Create one via the <see cref="DisplayValues"/> extensions.
/// </summary>
public record DisplayCell(string DisplayId) {
    // Captured at creation: the emitter handlers are bound to the executing cell's
    // parent message, so updates from background work (timers, progress loops) keep
    // publishing against the originating cell's output after the cell has finished.
    private readonly Action<DisplayData> _emit = DisplayDataEmitter.DisplayDataHandler;
    private readonly Action<DisplayData> _update = DisplayDataEmitter.UpdateDisplayDataHandler;
    private bool _isDisplayed;

    public IDisplayValue Value { get; private set; }

    public DisplayCell Update(object value, Type preferredDisplayType = null, string preferredMimeType = null) {
        Value = value is IDisplayValue concept
            ? concept
            : new DisplayObject(value, preferredDisplayType, preferredMimeType);
        var isUpdate = _isDisplayed;
        _isDisplayed = true;

        var data = DisplayDataPackager.Pack(Value);
        data.Transient["display_id"] = DisplayId;
        (isUpdate ? _update : _emit)?.Invoke(data);

        DisplayValues.Notify(this, isUpdate);
        return this;
    }
}
