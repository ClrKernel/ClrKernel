using ClrKernel.Core.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class DisplayProgressTest {
    // DisplayProgress publishes itself through a DisplayCell; the bar is drawn
    // by the registered renders.
    [ClassInitialize]
    public static void RegisterPlugin(TestContext _) =>
        ClrKernel.Formatting.Html.HtmlFormatters.RegisterDefaults();

    [ClassCleanup]
    public static void UnregisterPlugin() =>
        ClrKernel.Formatting.Html.HtmlFormatters.UnregisterDefaults();

    [TestMethod]
    public void Renders_percentage_on_report_and_done_message_when_finished() {
        string last = null;
        void OnCell(DisplayCell cell) =>
            last = (string)ClrKernel.Core.Scripting.MimeBundler.Bundle(cell).Data["text/html"];
        DisplayValues.OnCellDisplayed += OnCell;
        DisplayValues.OnCellUpdated += OnCell;
        try {
            var progress = new DisplayProgress("Loading", total: 100).Show();
            progress.Report(50);
            StringAssert.Contains(last, "50");
            StringAssert.Contains(last, "%");
            progress.Done(100);
            StringAssert.Contains(last, "done");
        } finally {
            DisplayValues.OnCellDisplayed -= OnCell;
            DisplayValues.OnCellUpdated -= OnCell;
        }
    }

    [TestMethod]
    public void Report_before_Show_still_displays() {
        var displays = 0;
        void OnCell(DisplayCell cell) => displays++;
        DisplayValues.OnCellDisplayed += OnCell;
        try {
            new DisplayProgress("work").Report(1);
            Assert.AreEqual(1, displays);
        } finally {
            DisplayValues.OnCellDisplayed -= OnCell;
        }
    }
}
