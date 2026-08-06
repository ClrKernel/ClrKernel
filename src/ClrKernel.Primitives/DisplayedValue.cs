using System;

namespace ClrKernel.Primitives {
    /// <summary>
    /// A handle to output already shown in the frontend that can be updated in
    /// place (Jupyter update_display_data). Create one with
    /// <see cref="DisplayDataEmitter.DisplayAs"/>; call <see cref="Update(string)"/>
    /// to replace the rendered content. The emit callback is captured at creation,
    /// so updates from background work (timers, progress loops) keep flowing to the
    /// originating cell's output even after the cell has finished executing.
    /// </summary>
    public class DisplayedValue {
        private readonly string _mimeType;
        private readonly Action<DisplayData> _emitUpdate;

        public string DisplayId { get; }

        public DisplayedValue(string displayId, string mimeType, Action<DisplayData> emitUpdate) {
            DisplayId = displayId;
            _mimeType = mimeType;
            _emitUpdate = emitUpdate;
        }

        /// <summary>Replaces the displayed content, keeping the original MIME type.</summary>
        public void Update(string content) {
            var data = new DisplayData();
            data.Data[_mimeType] = content ?? "";
            data.Transient["display_id"] = DisplayId;
            _emitUpdate?.Invoke(data);
        }

        /// <summary>Replaces the displayed content with the value's string form.</summary>
        public void Update(object content) {
            Update(content?.ToString() ?? "");
        }

        /// <summary>Replaces the displayed content with an explicit MIME bundle.</summary>
        public void Update(DisplayData data) {
            data.Transient["display_id"] = DisplayId;
            _emitUpdate?.Invoke(data);
        }
    }
}
