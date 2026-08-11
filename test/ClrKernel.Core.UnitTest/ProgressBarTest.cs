using ClrKernel.Core.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class ProgressBarTest {
    [TestMethod]
    public void Renders_percentage_on_report() {
        string last = null;
        var prevDisplay = DisplayDataEmitter.DisplayDataHandler;
        var prevUpdate = DisplayDataEmitter.UpdateDisplayDataHandler;
        try {
            DisplayDataEmitter.DisplayDataHandler = d => last = (string)d.Data["text/html"];
            DisplayDataEmitter.UpdateDisplayDataHandler = d => last = (string)d.Data["text/html"];
            var bar = new ProgressBar("Loading", 100);
            bar.Report(50);
            StringAssert.Contains(last, "50");
            StringAssert.Contains(last, "%");
            bar.Done(100);
            StringAssert.Contains(last, "done");
        } finally {
            DisplayDataEmitter.DisplayDataHandler = prevDisplay;
            DisplayDataEmitter.UpdateDisplayDataHandler = prevUpdate;
        }
    }
}
