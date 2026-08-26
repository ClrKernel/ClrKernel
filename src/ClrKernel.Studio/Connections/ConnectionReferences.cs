using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Studio;

/// <summary>
/// Which saved connections a notebook names.
/// <para>
/// This is what the private-connection warning and the promotion block are both
/// asking. A notebook is committed and runs for other people and for the scheduler,
/// so a cell naming a connection only its author can see fails everywhere else —
/// and it fails at run time, with a message about a name that is not found, which
/// is a bad way to learn it.
/// </para>
/// <para>
/// Nothing here knows what SQL is. A directive parameter's <c>ValueRole</c> says it
/// names a connection, and a provider descriptor says which of its flags define one
/// rather than refer to it. Both come from the kernel and the provider, so a
/// language added later is read correctly with no change here.
/// </para>
/// </summary>
public static class ConnectionReferences {
    /// <summary>
    /// The connection names the notebook's cells refer to, in the order found and
    /// without duplicates.
    /// </summary>
    /// <param name="descriptors">
    /// The connection providers this process can reason about. A connect directive
    /// belonging to a provider that is not here is skipped rather than guessed at:
    /// without its descriptor there is no way to tell "use the saved connection
    /// called x" from "define a connection called x right here", and mistaking the
    /// second for the first would block a promotion that was never in question.
    /// </param>
    public static IReadOnlyList<string> In(
        string notebook,
        IReadOnlyList<LanguageDescriptor> languages,
        IReadOnlyList<ConnectionProviderDescriptor> descriptors) {
        var found = new List<string>();
        if (string.IsNullOrEmpty(notebook)) {
            return found;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in NotebookMarkdown.Parse(notebook, languages)) {
            if (cell.Kind != CellKind.Code) {
                continue;
            }
            var language = NotebookMarkdown.LanguageForTag(cell.Tag, languages);
            if (language == null) {
                continue;
            }
            foreach (var line in cell.Source.Split('\n')) {
                foreach (var name in InLine(line.Trim(), language, descriptors)) {
                    if (seen.Add(name)) {
                        found.Add(name);
                    }
                }
            }
        }
        return found;
    }

    private static IEnumerable<string> InLine(
        string line, LanguageDescriptor language,
        IReadOnlyList<ConnectionProviderDescriptor> descriptors) {
        if (!line.StartsWith("#!", StringComparison.Ordinal)) {
            yield break;
        }
        // The whole first token, so #!sql-connect is never read as #!sql — the same
        // longest-selector rule the kernel's own dispatch follows, made structural
        // here by comparing the token rather than a prefix.
        var selector = line.Split(new[] { ' ', '\t' }, 2)[0];
        var definition = language.Directives.FirstOrDefault(d =>
            string.Equals(d.Selector, selector, StringComparison.OrdinalIgnoreCase));
        if (definition == null) {
            yield break;
        }
        var args = DirectiveParser.Parse(definition, line);

        // "Run this cell on connection x" — the parameter says so itself.
        foreach (var parameter in definition.Parameters.Where(p => p.ValueRole == "connection")) {
            foreach (var value in args.GetAll(parameter.Name)) {
                if (!string.IsNullOrWhiteSpace(value)) {
                    yield return value.Trim();
                }
            }
        }

        var descriptor = descriptors.FirstOrDefault(d =>
            string.Equals(d.ConnectSelector, selector, StringComparison.OrdinalIgnoreCase));
        if (descriptor == null) {
            yield break;
        }
        // A connect directive carrying only a name refers to a saved connection; one
        // carrying anything that shapes a connection is defining its own, and what it
        // defines is nobody's private entry. Which flags shape one is the provider's
        // own answer, read off its settings rather than copied into a list here.
        var naming = descriptor.Settings.FirstOrDefault(s => s.Required && s.DirectiveFlag != null);
        if (naming == null) {
            yield break;
        }
        var shaping = descriptor.Settings
            .Where(s => s.DirectiveFlag != null && !ReferenceEquals(s, naming))
            .Select(s => s.DirectiveFlag);
        if (shaping.Any(flag => args.Has(flag))) {
            yield break;
        }
        var name = args.Get(naming.DirectiveFlag);
        if (!string.IsNullOrWhiteSpace(name)) {
            yield return name.Trim();
        }
    }
}
