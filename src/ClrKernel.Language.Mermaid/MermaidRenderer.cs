using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Language.Mermaid;
/// <summary>
/// Renders Mermaid diagram source into self-contained notebook output. The
/// Mermaid library (MIT) is embedded in this assembly and inlined into every
/// rendered output, so diagrams draw <b>fully offline</b> — no CDN and no
/// network at view time. The output is theme-aware (follows the viewer's
/// light/dark preference) and degrades to showing the diagram source if the
/// library fails to run. Used by the <c>#!mermaid</c> cell language and the
/// <see cref="MermaidExtensions.DisplayMermaid(string)"/> helper.
/// </summary>
public static class MermaidRenderer {
    private const string _resourceName = "ClrKernel.Language.Mermaid.assets.mermaid.min.js";
    private static string _libraryJs;

    /// <summary>The embedded Mermaid library source (loaded once, then cached).</summary>
    public static string LibraryJs {
        get {
            if (_libraryJs == null) {
                _libraryJs = LoadEmbedded(_resourceName);
            }
            return _libraryJs;
        }
    }

    /// <summary>
    /// Builds a self-contained HTML document that renders <paramref name="source"/>
    /// as a Mermaid diagram offline. Safe to embed directly as a
    /// <c>text/html</c> output.
    /// </summary>
    public static string RenderHtml(string source) {
        var id = "clrkernel-mermaid-" + Guid.NewGuid().ToString("N");
        var svgId = id + "-svg";
        source = source ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("<div class=\"clrkernel-mermaid\" id=\"").Append(id).Append("\">");
        sb.Append(Style(id));

        // The source is kept (HTML-escaped) in a hidden node and read back via
        // textContent — so arrows like --> and characters like < survive the
        // HTML parse and reach Mermaid unchanged. It also feeds the fallback.
        sb.Append("<pre class=\"ck-mermaid-src\" id=\"").Append(id).Append("-src\" style=\"display:none\">")
            .Append(Encode(source)).Append("</pre>");
        sb.Append("<div class=\"ck-mermaid-out\" id=\"").Append(id).Append("-out\">Rendering diagram…</div>");

        // Inline the library, then render into this diagram's own container.
        sb.Append("<script>").Append(LibraryJs).Append("</script>");
        sb.Append("<script>").Append(InitScript(id, svgId)).Append("</script>");

        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the display bundle for a Mermaid diagram: a <c>text/html</c>
    /// rendered diagram and a <c>text/plain</c> source fallback.
    /// </summary>
    public static DisplayData Render(string source) {
        var data = new DisplayData();
        data.Data["text/plain"] = source ?? string.Empty;
        data.Data["text/html"] = RenderHtml(source);
        return data;
    }

    private static string InitScript(string id, string svgId) {
        // Reads the source, picks a theme from the viewer's color scheme, and
        // renders; any failure (including the library not loading) falls back
        // to showing the diagram source instead of an empty box.
        return
            "(function(){" +
            "var srcEl=document.getElementById('" + id + "-src');" +
            "var out=document.getElementById('" + id + "-out');" +
            "if(!srcEl||!out)return;" +
            "var code=srcEl.textContent;" +
            "function showSource(msg){out.innerHTML='';if(msg){var e=document.createElement('div');e.className='ck-mermaid-error';e.textContent=msg;out.appendChild(e);}var p=document.createElement('pre');p.className='ck-mermaid-code';p.textContent=code;out.appendChild(p);}" +
            "try{" +
            "if(typeof mermaid==='undefined'){showSource('Mermaid library did not load.');return;}" +
            "var dark=window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches;" +
            "mermaid.initialize({startOnLoad:false,theme:dark?'dark':'default',securityLevel:'strict'});" +
            "var res=mermaid.render('" + svgId + "',code);" +
            "if(res&&typeof res.then==='function'){res.then(function(r){out.innerHTML=r.svg;}).catch(function(err){showSource(String(err&&err.message||err));});}" +
            "else if(typeof res==='string'){out.innerHTML=res;}" +
            "}catch(e){showSource(String(e&&e.message||e));}" +
            "})();";
    }

    private static string Style(string id) {
        var s = "#" + id;
        return "<style>" +
            s + "{font-family:var(--vscode-font-family,-apple-system,Segoe UI,sans-serif);color:var(--vscode-foreground,#1f1f1f);padding:8px 4px;overflow:auto}" +
            s + " .ck-mermaid-out{display:flex;justify-content:center}" +
            s + " svg{max-width:100%;height:auto}" +
            s + " .ck-mermaid-error{color:var(--vscode-errorForeground,#cf222e);font-family:var(--vscode-editor-font-family,monospace);font-size:12px;margin-bottom:6px}" +
            s + " .ck-mermaid-code{white-space:pre;font-family:var(--vscode-editor-font-family,monospace);font-size:12px;text-align:left;background:var(--vscode-textCodeBlock-background,rgba(128,128,128,.08));padding:8px;border-radius:4px;overflow:auto}" +
            "</style>";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string LoadEmbedded(string resourceName) {
        var assembly = typeof(MermaidRenderer).GetTypeInfo().Assembly;
        using (var stream = assembly.GetManifestResourceStream(resourceName)) {
            if (stream == null) {
                throw new InvalidOperationException(
                    "Embedded resource not found: " + resourceName +
                    ". Available: " + string.Join(", ", assembly.GetManifestResourceNames()));
            }
            using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                return reader.ReadToEnd();
            }
        }
    }
}
