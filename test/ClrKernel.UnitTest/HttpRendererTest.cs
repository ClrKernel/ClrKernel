using ClrKernel.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class HttpRendererTest {
    [TestMethod]
    public void Renders_status_badge_method_and_url() {
        var exchange = new HttpExchange {
            RequestMethod = "GET",
            RequestUrl = "https://example.com/x",
            StatusCode = 200,
            ReasonPhrase = "OK",
            ContentType = "application/json",
            BodyText = "{\"a\":1}",
            ContentLength = 7,
            ElapsedMs = 12,
        };
        var (html, text) = HttpResponseRenderer.Render(exchange);

        StringAssert.Contains(html, "clrkernel-http");
        StringAssert.Contains(html, "ck-2xx");           // 200 -> green class
        StringAssert.Contains(html, ">200 OK<");
        StringAssert.Contains(html, "GET");
        StringAssert.Contains(html, "https://example.com/x");
        StringAssert.Contains(html, "ck-json-key");       // highlighted JSON key
        StringAssert.Contains(text, "200 OK");
    }

    [TestMethod]
    public void Status_classes_track_status_ranges() {
        StringAssert.Contains(HttpResponseRenderer.Render(new HttpExchange { StatusCode = 404 }).Html, "ck-4xx");
        StringAssert.Contains(HttpResponseRenderer.Render(new HttpExchange { StatusCode = 503 }).Html, "ck-5xx");
        StringAssert.Contains(HttpResponseRenderer.Render(new HttpExchange { StatusCode = 302 }).Html, "ck-3xx");
    }

    [TestMethod]
    public void Escapes_html_in_body() {
        var exchange = new HttpExchange {
            StatusCode = 200,
            ContentType = "text/plain",
            BodyText = "<script>alert(1)</script>",
        };
        var (html, _) = HttpResponseRenderer.Render(exchange);
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<script>alert"), "raw script leaked into output");
    }

    [TestMethod]
    public void Error_exchange_renders_error_card() {
        var exchange = new HttpExchange {
            RequestMethod = "GET",
            RequestUrl = "https://nope.invalid/",
            Error = "No such host is known.",
        };
        var (html, text) = HttpResponseRenderer.Render(exchange);
        StringAssert.Contains(html, "Request failed");
        StringAssert.Contains(html, "No such host is known.");
        StringAssert.Contains(text, "Request failed");
    }

    [TestMethod]
    public void Renders_image_inline_as_data_uri() {
        var exchange = new HttpExchange {
            StatusCode = 200,
            ContentType = "image/png",
            BodyBytes = new byte[] { 1, 2, 3, 4 },
            ContentLength = 4,
        };
        var (html, _) = HttpResponseRenderer.Render(exchange);
        StringAssert.Contains(html, "data:image/png;base64,");
        StringAssert.Contains(html, "<img");
    }
}
