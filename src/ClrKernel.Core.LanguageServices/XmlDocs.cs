using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace ClrKernel.Core.LanguageServices;

/// <summary>
/// Extracts readable text from a symbol's XML documentation comment. Works for
/// metadata symbols only when the compilation's references carry documentation
/// providers — see <c>ScriptLanguageService.ReferenceFor</c>.
/// </summary>
internal static class XmlDocs {
    /// <summary>The flattened &lt;summary&gt; text, or null when there is none.</summary>
    public static string Summary(ISymbol symbol) => Section(symbol, "summary", null);

    /// <summary>The flattened &lt;param name="..."&gt; text, or null when there is none.</summary>
    public static string Param(ISymbol symbol, string parameterName) => Section(symbol, "param", parameterName);

    private static string Section(ISymbol symbol, string element, string nameAttribute) {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) {
            return null;
        }
        try {
            var doc = XDocument.Parse(xml);
            var match = doc.Descendants(element).FirstOrDefault(e =>
                nameAttribute == null || (string)e.Attribute("name") == nameAttribute);
            if (match == null) {
                return null;
            }
            var text = Regex.Replace(Flatten(match), @"\s+", " ").Trim();
            return text.Length == 0 ? null : text;
        } catch (System.Xml.XmlException) {
            return null;
        }
    }

    // <see cref="T:System.String"/> and friends carry their content in an
    // attribute, which XElement.Value drops; substitute the short name.
    private static string Flatten(XElement element) {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes()) {
            switch (node) {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    var attr = child.Attribute("cref") ?? child.Attribute("name") ?? child.Attribute("langword");
                    sb.Append(attr != null ? ShortName(attr.Value) : Flatten(child));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string ShortName(string cref) {
        var name = cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0) {
            name = name.Substring(0, paren);
        }
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot + 1) : name;
    }
}
