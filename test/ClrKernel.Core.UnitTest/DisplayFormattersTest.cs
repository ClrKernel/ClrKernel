using System;
using System.Collections.Generic;
using ClrKernel.Core.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel;

[TestClass]
public class DisplayFormattersTest {
    [TestMethod]
    public void AValueAlreadyOfTheTargetTypePassesThrough() {
        var html = new DisplayHtml("<b>x</b>");
        Assert.AreSame(html, DisplayFormatters.Format<DisplayHtml>(html));
    }

    [TestMethod]
    public void ARegisteredFormatterConverts() {
        var registered = DisplayFormatters.Register<DisplayConsoleText, DisplayHtml>(
            c => new DisplayHtml("<pre>" + c.ConsoleOutput + "</pre>"));
        try {
            var html = new DisplayConsoleText("hi").ToHtml();
            Assert.AreEqual("<pre>hi</pre>", html.Html);
        } finally {
            DisplayFormatters.Unregister(registered);
        }
    }

    [TestMethod]
    public void TwoFormattersChainThroughAnIntermediateConcept() {
        var toTable = DisplayFormatters.Register<DisplayConsoleText, DisplayTable>(
            c => new DisplayTable(c, new[] { "Line" }, new[] { new[] { c.ConsoleOutput } }));
        var toHtml = DisplayFormatters.Register<DisplayTable, DisplayHtml>(
            t => new DisplayHtml("<table>" + t.Rows[0][0] + "</table>"));
        try {
            // No direct ConsoleText -> Html registration: must go via DisplayTable.
            var html = DisplayFormatters.Format<DisplayHtml>(new DisplayConsoleText("row"));
            Assert.AreEqual("<table>row</table>", html.Html);
        } finally {
            DisplayFormatters.Unregister(toTable);
            DisplayFormatters.Unregister(toHtml);
        }
    }

    [TestMethod]
    public void TheNewestRegistrationWinsAndUnregisterRestoresTheOlder() {
        var first = DisplayFormatters.Register<DisplayText, DisplayHtml>(t => new DisplayHtml("first"));
        var second = DisplayFormatters.Register<DisplayText, DisplayHtml>(t => new DisplayHtml("second"));
        try {
            Assert.AreEqual("second", new DisplayText("x").ToHtml().Html, "later registration must override");
            DisplayFormatters.Unregister(second);
            Assert.AreEqual("first", new DisplayText("x").ToHtml().Html, "unregistering must fall back");
        } finally {
            DisplayFormatters.Unregister(first);
            DisplayFormatters.Unregister(second);
        }
    }

    [TestMethod]
    public void AnUnknownConversionThrowsANamingException() {
        var e = Assert.ThrowsExactly<InvalidOperationException>(
            () => DisplayFormatters.Format<DisplayProgress>(new DisplayHtml("x")));
        StringAssert.Contains(e.Message, nameof(DisplayHtml));
        StringAssert.Contains(e.Message, nameof(DisplayProgress));
    }

    [TestMethod]
    public void TheBuiltInFallbackTurnsAnyObjectIntoText() {
        Assert.AreEqual("42", new DisplayObject(42).ToText().Text);
        Assert.AreEqual("", new DisplayObject(null).ToText().Text);
    }

    [TestMethod]
    public void APreferredStringConceptCoercesRatherThanFormats() {
        // "my value IS html" — even when a rich object->html formatter is registered.
        var rich = DisplayFormatters.Register<DisplayObject, DisplayHtml>(o => new DisplayHtml("rendered"));
        try {
            var resolved = DisplayFormatters.Resolve(new DisplayObject("<b>x</b>", typeof(DisplayHtml)));
            Assert.AreEqual("<b>x</b>", ((DisplayHtml)resolved).Html);
        } finally {
            DisplayFormatters.Unregister(rich);
        }
    }

    [TestMethod]
    public void APreferredStructuralConceptResolvesThroughTheRegistry() {
        var extract = DisplayFormatters.Register<DisplayObject, DisplayTable>(
            o => new DisplayTable(o.Value, new[] { "Value" }, new[] { new[] { o.Value.ToString() } }));
        try {
            var resolved = DisplayFormatters.Resolve(new DisplayObject(7, typeof(DisplayTable)));
            Assert.AreEqual("7", ((DisplayTable)resolved).Rows[0][0]);
        } finally {
            DisplayFormatters.Unregister(extract);
        }
    }

    [TestMethod]
    public void AnUnsatisfiablePreferenceFallsBackToTheRawObject() {
        var obj = new DisplayObject(7, typeof(DisplayTable)); // nothing registered for tables
        Assert.AreSame(obj, DisplayFormatters.Resolve(obj));
        Assert.AreEqual("7", obj.ToText().Text, "text fallback must still work");
    }
}

[TestClass]
public class DisplayDataPackagerTest {
    [TestMethod]
    public void BytesArePackagedAsBase64UnderTheirMimeType() {
        var data = DisplayDataPackager.Pack(new DisplayBytes(new byte[] { 1, 2, 3 }, "image/png"));
        Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2, 3 }), data.Data["image/png"]);
        Assert.IsFalse(data.Data.ContainsKey("text/plain"), "binary output carries no text form");
    }

    [TestMethod]
    public void AnExplicitMimePreferencePublishesVerbatim() {
        var data = DisplayDataPackager.Pack(new DisplayObject("<svg/>", null, "image/svg+xml"));
        Assert.AreEqual("<svg/>", data.Data["image/svg+xml"]);
    }

    [TestMethod]
    public void MarkdownKeepsItsOwnMimeTypeAlongsideThePlainFallback() {
        var data = DisplayDataPackager.Pack(new DisplayMarkdown("# hi"));
        Assert.AreEqual("# hi", data.Data["text/markdown"]);
        Assert.AreEqual("# hi", data.Data["text/plain"]);
    }

    [TestMethod]
    public void WithoutAnHtmlFormatterOnlyTextIsPackaged() {
        // Test-class ordering isn't contractual: make sure the plugin's defaults
        // (registered by HtmlFormattersTest) are gone regardless of who ran first.
        Formatting.Html.HtmlFormatters.UnregisterDefaults();
        var data = DisplayDataPackager.Pack(new DisplayObject(42));
        Assert.AreEqual("42", data.Data["text/plain"]);
        Assert.IsFalse(data.Data.ContainsKey("text/html"));
    }

    [TestMethod]
    public void WithAnHtmlFormatterBothMimeTypesArePackaged() {
        var rich = DisplayFormatters.Register<DisplayObject, DisplayHtml>(
            o => new DisplayHtml("<i>" + o.Value + "</i>"));
        try {
            var data = DisplayDataPackager.Pack(new DisplayObject(42));
            Assert.AreEqual("<i>42</i>", data.Data["text/html"]);
            Assert.AreEqual("42", data.Data["text/plain"]);
        } finally {
            DisplayFormatters.Unregister(rich);
        }
    }
}

[TestClass]
public class DisplayCellTest {
    [TestMethod]
    public void FirstDisplayEmitsThenUpdatesRouteToTheUpdateHandler() {
        var displays = new List<DisplayData>();
        var updates = new List<DisplayData>();
        var displayedEvents = 0;
        var updatedEvents = 0;
        Action<DisplayCell> onDisplayed = _ => displayedEvents++;
        Action<DisplayCell> onUpdated = _ => updatedEvents++;
        DisplayValues.OnCellDisplayed += onDisplayed;
        DisplayValues.OnCellUpdated += onUpdated;
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            DisplayDataEmitter.UpdateDisplayDataHandler = updates.Add;

            var cell = "hello".Display();

            Assert.AreEqual(1, displays.Count);
            Assert.AreEqual(0, updates.Count, "the very first display must not be an update");
            Assert.AreEqual("hello", displays[0].Data["text/plain"]);
            Assert.AreEqual(cell.DisplayId, displays[0].Transient["display_id"]);
            Assert.AreEqual(1, displayedEvents);
            Assert.AreEqual(0, updatedEvents);

            cell.Update("again");

            Assert.AreEqual(1, displays.Count, "updates must not emit new display_data");
            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual("again", updates[0].Data["text/plain"]);
            Assert.AreEqual(cell.DisplayId, updates[0].Transient["display_id"]);
            Assert.AreEqual(1, updatedEvents);
        } finally {
            DisplayValues.OnCellDisplayed -= onDisplayed;
            DisplayValues.OnCellUpdated -= onUpdated;
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
        }
    }

    [TestMethod]
    public void HandlersAreCapturedAtCreationForBackgroundUpdates() {
        var updates = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = _ => { };
            DisplayDataEmitter.UpdateDisplayDataHandler = updates.Add;
            var cell = "x".Display();

            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;

            cell.Update("from background");
            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual("from background", updates[0].Data["text/plain"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
        }
    }

    [TestMethod]
    public void DisplayHtmlTreatsTheValueAsHtml() {
        var displays = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            "<b>bold</b>".DisplayHtml();
            Assert.AreEqual("<b>bold</b>", displays[0].Data["text/html"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
        }
    }

    [TestMethod]
    public void AConceptValuePassesStraightThroughToTheCell() {
        var displays = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            var cell = new DisplayMarkdown("# title").Display();
            Assert.IsInstanceOfType(cell.Value, typeof(DisplayMarkdown));
            Assert.AreEqual("# title", displays[0].Data["text/markdown"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
        }
    }
}
