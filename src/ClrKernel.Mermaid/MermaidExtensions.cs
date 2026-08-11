using ClrKernel.Core.Primitives;

namespace ClrKernel.Mermaid;
/// <summary>
/// C# cell helpers for rendering Mermaid diagrams. Available in notebook
/// cells via the <c>ClrKernel.Mermaid</c> namespace import, so a diagram
/// built programmatically can be shown with
/// <c>mermaidSource.DisplayMermaid()</c> — mirroring the display helpers in
/// <see cref="DisplayExtensions"/>.
/// </summary>
public static class MermaidExtensions {
    /// <summary>
    /// Renders Mermaid <paramref name="source"/> as a diagram and returns an
    /// updatable display handle (e.g.
    /// <c>"graph TD; A--&gt;B".DisplayMermaid()</c>).
    /// </summary>
    public static DisplayedValue DisplayMermaid(this string source) {
        return MermaidRenderer.RenderHtml(source).DisplayAs("text/html");
    }
}
