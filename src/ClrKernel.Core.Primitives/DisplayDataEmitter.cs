using System;
using System.Collections.Generic;

namespace ClrKernel.Core.Primitives {
    public static class DisplayDataEmitter {
        public static Action<DisplayData> DisplayDataHandler { get; set; }

        public static Action<DisplayData> UpdateDisplayDataHandler { get; set; }

        public static void Emit(DisplayData data) {
            DisplayDataHandler?.Invoke(data);
        }


        public static void EmitHtml(string html) {
            Emit(new DisplayData {
                Data = new Dictionary<string, object>
                {
                    { "text/html", html }
                }
            });
        }

        public static void EmitText(string text) {
            Emit(new DisplayData {
                Data = new Dictionary<string, object>
                {
                    { "text/plain", text }
                }
            });
        }
    }
}
