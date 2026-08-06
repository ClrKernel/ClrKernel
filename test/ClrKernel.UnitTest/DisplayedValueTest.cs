using System.Collections.Generic;
using ClrKernel.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel;

[TestClass]
public class DisplayedValueTest {
    [TestMethod]
    public void DisplayAsEmitsThenUpdateRoutesToUpdateHandler() {
        var displays = new List<DisplayData>();
        var updates = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            DisplayDataEmitter.UpdateDisplayDataHandler = updates.Add;

            var dv = "initial".DisplayAs("text/html");

            Assert.AreEqual(1, displays.Count);
            Assert.AreEqual("initial", (string)displays[0].Data["text/html"]);
            var displayId = (string)displays[0].Transient["display_id"];
            Assert.AreEqual(dv.DisplayId, displayId);

            dv.Update("<b>updated</b>");
            dv.Update(42);

            Assert.AreEqual(1, displays.Count, "updates must not emit new display_data");
            Assert.AreEqual(2, updates.Count);
            Assert.AreEqual("<b>updated</b>", (string)updates[0].Data["text/html"]);
            Assert.AreEqual("42", (string)updates[1].Data["text/html"]);
            Assert.AreEqual(displayId, (string)updates[0].Transient["display_id"]);
            Assert.AreEqual(displayId, (string)updates[1].Transient["display_id"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
        }
    }

    [TestMethod]
    public void UpdateHandlerIsCapturedAtCreation() {
        var updates = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = _ => { };
            DisplayDataEmitter.UpdateDisplayDataHandler = updates.Add;
            var dv = "x".DisplayAs("text/plain");

            // Simulate the cell finishing: the engine clears the static handlers,
            // but a background timer holding dv can still publish updates.
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;

            dv.Update("from background");
            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual("from background", (string)updates[0].Data["text/plain"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
        }
    }
}
