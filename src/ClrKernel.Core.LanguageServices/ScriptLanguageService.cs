using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace ClrKernel.Core.LanguageServices;

/// <summary>
/// Roslyn C# script language services. Given a <see cref="ScriptStateSnapshot"/>
/// (the live engine's references, imports, and prior submissions) plus the code
/// being edited and a caret offset, produces completion, hover, and signature
/// help that see everything the running session sees. Editor-neutral: the LSP
/// server and the Jupyter kernel both consume these DTOs.
/// </summary>
public sealed class ScriptLanguageService {
    // The Features assemblies aren't in MefHostServices.DefaultAssemblies, so
    // CompletionService/QuickInfoService wouldn't resolve without adding them.
    private static readonly MefHostServices _host = CreateHost();

    private static MefHostServices CreateHost() {
        var assemblies = MefHostServices.DefaultAssemblies
            .Concat(new[] {
                Assembly.Load("Microsoft.CodeAnalysis.Features"),
                Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
            })
            .Distinct();
        return MefHostServices.Create(assemblies);
    }

    // Directive lines are dropped from replayed submissions: the resolved
    // references are supplied directly, and Roslyn's default resolver doesn't
    // understand `#r "nuget:"`. Keep everything else (usings, code, classes).
    private static bool IsDirectiveLine(string line) {
        var t = line.TrimStart();
        return t.StartsWith("#r", StringComparison.Ordinal)
            || t.StartsWith("#i", StringComparison.Ordinal)
            || t.StartsWith("#load", StringComparison.Ordinal)
            || t.StartsWith("#!", StringComparison.Ordinal);
    }

    private static string StripDirectives(string submission) {
        if (submission.IndexOf('#') < 0) {
            return submission;
        }
        var kept = submission.Replace("\r\n", "\n").Split('\n').Where(l => !IsDirectiveLine(l));
        return string.Join("\n", kept);
    }

    /// <summary>
    /// Builds a single script document = imports + replayed prior submissions +
    /// the current code, and returns it with the caret mapped into that document.
    /// </summary>
    private (Document document, int position, int prefixLength) BuildDocument(
        ScriptStateSnapshot snapshot, string code, int position) {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(snapshot.Preamble)) {
            sb.Append(snapshot.Preamble).Append('\n');
        }
        foreach (var ns in snapshot.Imports) {
            sb.Append("using ").Append(ns).Append(";\n");
        }
        foreach (var submission in snapshot.Submissions) {
            sb.Append(StripDirectives(submission)).Append('\n');
        }
        var prefixLength = sb.Length;
        sb.Append(code);
        var fullText = sb.ToString();

        var workspace = new AdhocWorkspace(_host);
        var parseOptions = new CSharpParseOptions(kind: SourceCodeKind.Script, languageVersion: LanguageVersion.Preview);
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            usings: snapshot.Imports.ToImmutableArray(),
            metadataReferenceResolver: null,
            sourceReferenceResolver: null)
            .WithSpecificDiagnosticOptions(new[] {
                // Script top-level statements can trip these in a merged doc.
                new KeyValuePair<string, ReportDiagnostic>("CS7022", ReportDiagnostic.Suppress),
            });

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "ClrKernelScript", "ClrKernelScript", LanguageNames.CSharp,
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            metadataReferences: BuildReferences(snapshot));

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Script.csx", SourceText.From(fullText));
        return (document, prefixLength + position, prefixLength);
    }

    /// <summary>
    /// References for the completion compilation: the loaded runtime assemblies
    /// (BCL + ClrKernel + any nuget already loaded by execution) unioned with the
    /// engine's explicit references, deduped by file path. Loaded assemblies give
    /// completion the same surface execution sees once a cell has run.
    /// </summary>
    private static IReadOnlyList<MetadataReference> BuildReferences(ScriptStateSnapshot snapshot) {
        // Keyed by assembly FILE NAME, not path: the same assembly loaded from two
        // places (the host's copy and a nuget-restored one, or two package versions)
        // would otherwise put duplicate identities into the compilation, and symbol
        // resolution for every type they touch silently fails.
        var byName = new Dictionary<string, MetadataReference>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies()) {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) {
                continue;
            }
            var fileName = Path.GetFileName(assembly.Location);
            // Cell-compiled libraries stay loaded after being superseded by a
            // re-run; only the engine's references (below) know the live one.
            if (fileName.StartsWith(InteractiveScriptEngine.CellLibraryPrefix, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            byName.TryAdd(fileName, ReferenceFor(assembly.Location));
        }

        // The engine's explicit references WIN over loaded assemblies — they are
        // the versions cell execution actually uses. Only resolvable file-based
        // references are valid in a fresh compilation; by-name/unresolved ones
        // (framework refs) are already covered above by the loaded assemblies.
        foreach (var reference in snapshot.References) {
            if (reference is PortableExecutableReference pe && !string.IsNullOrEmpty(pe.FilePath)) {
                byName[Path.GetFileName(pe.FilePath)] = ReferenceFor(pe.FilePath);
            }
        }

        return byName.Values.ToArray();
    }

    // References cached per path with XML documentation attached, so hover,
    // completion and signature help can show /// summaries. Nuget packages and
    // ClrKernel ship the .xml beside the dll; the shared framework doesn't, but
    // its ref pack does. The cache also keeps per-keystroke document builds cheap.
    private static readonly ConcurrentDictionary<string, MetadataReference> _referenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static MetadataReference ReferenceFor(string path) =>
        _referenceCache.GetOrAdd(path, p => {
            var xml = FindXmlDocs(p);
            return MetadataReference.CreateFromFile(
                p, documentation: xml == null ? null : XmlDocumentationProvider.CreateFromFile(xml));
        });

    private static string FindXmlDocs(string assemblyPath) {
        var sibling = Path.ChangeExtension(assemblyPath, ".xml");
        if (File.Exists(sibling)) {
            return sibling;
        }
        // <root>/shared/<framework>/<version>/Foo.dll has no docs; the matching
        // ref pack <root>/packs/<framework>.Ref/<version>/ref/<tfm>/Foo.xml does.
        var versionDir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath) ?? ".");
        var frameworkDir = versionDir.Parent;
        if (frameworkDir?.Parent is not { } sharedDir
            || !sharedDir.Name.Equals("shared", StringComparison.OrdinalIgnoreCase)
            || sharedDir.Parent == null) {
            return null;
        }
        var refRoot = Path.Combine(sharedDir.Parent.FullName, "packs", frameworkDir.Name + ".Ref", versionDir.Name, "ref");
        if (!Directory.Exists(refRoot)) {
            return null;
        }
        var fileName = Path.GetFileNameWithoutExtension(assemblyPath) + ".xml";
        return Directory.EnumerateDirectories(refRoot)
            .Select(tfm => Path.Combine(tfm, fileName))
            .FirstOrDefault(File.Exists);
    }

    // --- Completion --------------------------------------------------------

    public async Task<CompletionResultDto> GetCompletionsAsync(
        ScriptStateSnapshot snapshot, string code, int position, CancellationToken cancellationToken = default) {
        var (document, pos, prefix) = BuildDocument(snapshot, code, position);
        var service = CompletionService.GetService(document);
        if (service == null) {
            return new CompletionResultDto(position, 0, Array.Empty<CompletionItemDto>());
        }

        var completions = await service.GetCompletionsAsync(document, pos, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (completions == null) {
            return new CompletionResultDto(position, 0, Array.Empty<CompletionItemDto>());
        }

        var items = new List<CompletionItemDto>(completions.ItemsList.Count);
        foreach (var item in completions.ItemsList) {
            items.Add(new CompletionItemDto(
                Label: item.DisplayText,
                InsertText: item.DisplayText,
                SortText: item.SortText ?? item.DisplayText,
                FilterText: item.FilterText ?? item.DisplayText,
                Kind: MapKind(item.Tags),
                Detail: item.InlineDescription ?? string.Empty));
        }

        // Kept so completionItem/resolve can lazily fetch one item's description
        // (signature + /// summary) by its index, IDE-style, without paying that
        // cost for the whole list up front.
        _lastCompletion = (document, service, completions.ItemsList);

        // Span of existing text being replaced, mapped back to cell coordinates.
        var replaceStart = Math.Max(0, completions.Span.Start - prefix);
        return new CompletionResultDto(replaceStart, completions.Span.Length, items);
    }

    private (Document Document, CompletionService Service, IReadOnlyList<CompletionItem> Items) _lastCompletion;

    /// <summary>
    /// The description (signature and documentation) of item <paramref name="index"/>
    /// from the most recent <see cref="GetCompletionsAsync"/> call, or null.
    /// </summary>
    public async Task<string> GetCompletionDocumentationAsync(int index, CancellationToken cancellationToken = default) {
        var (document, service, items) = _lastCompletion;
        if (document == null || items == null || index < 0 || index >= items.Count) {
            return null;
        }
        var description = await service.GetDescriptionAsync(document, items[index], cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(description?.Text) ? null : description.Text;
    }

    private static string MapKind(ImmutableArray<string> tags) {
        if (tags.IsDefaultOrEmpty) {
            return "Text";
        }
        // First tag is the symbol kind; the LSP/Jupyter layer maps it further.
        return tags[0];
    }

    // --- Definition --------------------------------------------------------

    /// <summary>
    /// Where the symbol at <paramref name="position"/> is defined. Source symbols come
    /// back as locations (current cell by offset, earlier executed submissions by their
    /// defining line); a metadata symbol — BCL, nuget, ClrKernel, anything referenced
    /// without source — comes back as decompiled C# so the host can peek it.
    /// </summary>
    public async Task<DefinitionResultDto> GetDefinitionsAsync(
        ScriptStateSnapshot snapshot, string code, int position, CancellationToken cancellationToken = default) {
        var empty = new DefinitionResultDto(Array.Empty<DefinitionLocationDto>(), null);
        var (document, pos, prefix) = BuildDocument(snapshot, code, position);
        // A cell's `using` line lands mid-script in the merged doc, where a using
        // directive is illegal and parser recovery mangles it — resolve it from
        // the raw cell text instead of the tree.
        var symbol = await UsingLineSymbolAsync(document, code, position, cancellationToken).ConfigureAwait(false)
            ?? await SymbolFinder.FindSymbolAtPositionAsync(document, pos, cancellationToken).ConfigureAwait(false)
            ?? await FallbackSymbolAsync(document, pos, cancellationToken).ConfigureAwait(false);
        if (symbol == null) {
            return empty;
        }

        // A namespace (F12 on a using directive) has no single definition; peek
        // an overview of its public types instead.
        if (symbol is INamespaceSymbol ns) {
            return new DefinitionResultDto(Array.Empty<DefinitionLocationDto>(), DecompiledSource.ForNamespace(ns));
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DefinitionLocationDto>();
        foreach (var location in symbol.Locations) {
            if (!location.IsInSource) {
                continue;
            }
            var span = location.SourceSpan;
            if (span.Start >= prefix) {
                // The whole declaration (method body and all) lets a peek frame the
                // entire member instead of one line.
                var fullStart = -1;
                var fullLength = 0;
                foreach (var reference in symbol.DeclaringSyntaxReferences) {
                    if (reference.SyntaxTree == location.SourceTree && reference.Span.Start >= prefix) {
                        fullStart = reference.Span.Start - prefix;
                        fullLength = reference.Span.Length;
                        break;
                    }
                }
                results.Add(new DefinitionLocationDto(
                    true, span.Start - prefix, span.Length, null, 0, fullStart, fullLength));
            } else {
                var line = text.Lines.GetLineFromPosition(span.Start);
                results.Add(new DefinitionLocationDto(
                    false, 0, span.Length, line.ToString(), span.Start - line.Start));
            }
        }
        if (results.Count > 0) {
            return new DefinitionResultDto(results, null);
        }

        var metadata = await DecompiledSource.ForSymbolAsync(document, symbol, cancellationToken)
            .ConfigureAwait(false);
        return new DefinitionResultDto(Array.Empty<DefinitionLocationDto>(), metadata);
    }

    /// <summary>
    /// When the caret sits on a <c>using Foo.Bar;</c> (or <c>using static T</c> /
    /// alias) line of the cell, resolves the dotted name against the compilation —
    /// a namespace symbol, or the named type for the static/alias forms.
    /// </summary>
    private static async Task<ISymbol> UsingLineSymbolAsync(
        Document document, string code, int position, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(code)) {
            return null;
        }
        var caret = Math.Min(Math.Max(position, 0), code.Length);
        var lineStart = caret == 0 ? 0 : code.LastIndexOf('\n', caret - 1) + 1;
        var lineEnd = caret >= code.Length ? code.Length : code.IndexOf('\n', caret);
        if (lineEnd < 0) {
            lineEnd = code.Length;
        }
        var line = code.Substring(lineStart, lineEnd - lineStart).Trim().TrimEnd(';').Trim();
        if (!line.StartsWith("using ", StringComparison.Ordinal)) {
            return null;
        }
        var name = line.Substring("using ".Length).Trim();
        if (name.StartsWith("static ", StringComparison.Ordinal)) {
            name = name.Substring("static ".Length).Trim();
        }
        var equals = name.IndexOf('=');
        if (equals >= 0) {
            name = name.Substring(equals + 1).Trim();
        }
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_')) {
            return null;
        }

        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation == null) {
            return null;
        }
        var parts = name.Split('.');
        INamespaceSymbol ns = compilation.GlobalNamespace;
        for (var i = 0; i < parts.Length - 1 && ns != null; i++) {
            ns = ns.GetNamespaceMembers().FirstOrDefault(n => n.Name == parts[i]);
        }
        if (ns == null) {
            return null;
        }
        var last = parts[^1];
        return (ISymbol)ns.GetNamespaceMembers().FirstOrDefault(n => n.Name == last)
            ?? ns.GetTypeMembers(last).FirstOrDefault();
    }

    // SymbolFinder wants the caret ON a name token; an IDE's F12 is more
    // forgiving. Retry via the semantic model at the caret and one position
    // left (caret at a token's end touches the NEXT token), accepting candidate
    // symbols too — e.g. an overload that didn't fully bind.
    private static async Task<ISymbol> FallbackSymbolAsync(Document document, int pos, CancellationToken cancellationToken) {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root == null || model == null || root.FullSpan.End == 0) {
            return null;
        }
        foreach (var probe in new[] { pos, pos - 1 }) {
            var clamped = Math.Max(0, Math.Min(probe, root.FullSpan.End - 1));
            var token = root.FindToken(clamped);
            for (var node = token.Parent; node != null; node = node.Parent) {
                var info = model.GetSymbolInfo(node, cancellationToken);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol != null) {
                    return symbol;
                }
                if (node is StatementSyntax or MemberDeclarationSyntax) {
                    break;
                }
            }
        }
        return null;
    }

    // --- Hover / quick info ------------------------------------------------

    public async Task<HoverDto> GetHoverAsync(
        ScriptStateSnapshot snapshot, string code, int position, CancellationToken cancellationToken = default) {
        var (document, pos, prefix) = BuildDocument(snapshot, code, position);
        var service = QuickInfoService.GetService(document);
        if (service == null) {
            return null;
        }

        var info = await service.GetQuickInfoAsync(document, pos, cancellationToken).ConfigureAwait(false);
        if (info == null) {
            return null;
        }

        // The Description section is a signature (rendered as C# by the host);
        // everything else — /// summaries, exceptions, usage — is prose.
        string description = null;
        var docs = new StringBuilder();
        foreach (var section in info.Sections) {
            var text = section.Text;
            if (string.IsNullOrEmpty(text)) {
                continue;
            }
            if (description == null && section.Kind == QuickInfoSectionKinds.Description) {
                description = text;
                continue;
            }
            if (docs.Length > 0) {
                docs.Append("\n\n");
            }
            docs.Append(text);
        }

        var start = Math.Max(0, info.Span.Start - prefix);
        return new HoverDto(description ?? docs.ToString(), start, info.Span.Length,
            description == null ? null : NullIfEmpty(docs.ToString()));
    }

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // --- Signature help (semantic-model based, public APIs only) -----------

    private static readonly SymbolDisplayFormat _signatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public async Task<SignatureHelpDto> GetSignatureHelpAsync(
        ScriptStateSnapshot snapshot, string code, int position, CancellationToken cancellationToken = default) {
        var (document, pos, _) = BuildDocument(snapshot, code, position);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root == null || model == null) {
            return null;
        }

        // Walk up from the token just before the caret (e.g. the "(" of an
        // incomplete call) to the enclosing invocation or object creation.
        var probe = Math.Max(0, Math.Min(pos, root.FullSpan.End) - 1);
        var token = root.FindToken(probe);
        var invoked = token.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax);
        if (invoked == null) {
            return null;
        }

        SyntaxNode argList = invoked switch {
            InvocationExpressionSyntax inv => inv.ArgumentList,
            ObjectCreationExpressionSyntax oc => oc.ArgumentList,
            _ => null,
        };
        // Only offer signature help once the caret is past the opening paren.
        var openParenEnd = (argList as ArgumentListSyntax)?.OpenParenToken.Span.End;
        if (argList == null || openParenEnd == null || pos < openParenEnd) {
            return null;
        }

        var methods = GetCandidateMethods(model, invoked, cancellationToken);
        if (methods.Count == 0) {
            return null;
        }

        var activeParameter = CountActiveParameter(argList, pos);

        var signatures = methods
            .Select(m => new SignatureDto(
                Label: m.ToDisplayString(_signatureFormat),
                Documentation: XmlDocs.Summary(m) ?? string.Empty,
                Parameters: m.Parameters.Select(p =>
                    new ParameterDto(p.ToDisplayString(_signatureFormat), XmlDocs.Param(m, p.Name) ?? string.Empty)).ToArray()))
            .ToArray();

        // Prefer an overload whose parameter count can satisfy the active index.
        var active = 0;
        for (int i = 0; i < methods.Count; i++) {
            if (methods[i].Parameters.Length > activeParameter) {
                active = i;
                break;
            }
        }

        return new SignatureHelpDto(signatures, active, activeParameter);
    }

    private static IReadOnlyList<IMethodSymbol> GetCandidateMethods(
        SemanticModel model, SyntaxNode invoked, CancellationToken cancellationToken) {
        switch (invoked) {
            case InvocationExpressionSyntax invocation: {
                    var group = model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>();
                    var symbol = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                    var all = group.ToList();
                    if (symbol != null && !all.Contains(symbol, SymbolEqualityComparer.Default)) {
                        all.Insert(0, symbol);
                    }
                    return all;
                }
            case ObjectCreationExpressionSyntax creation: {
                    var type = model.GetTypeInfo(creation, cancellationToken).Type
                               ?? model.GetSymbolInfo(creation.Type, cancellationToken).Symbol as ITypeSymbol;
                    if (type is INamedTypeSymbol named) {
                        return named.InstanceConstructors.ToList();
                    }
                    return Array.Empty<IMethodSymbol>();
                }
            default:
                return Array.Empty<IMethodSymbol>();
        }
    }

    private static int CountActiveParameter(SyntaxNode argList, int position) {
        var commas = argList switch {
            ArgumentListSyntax al => al.Arguments.GetSeparators(),
            AttributeArgumentListSyntax aal => aal.Arguments.GetSeparators(),
            _ => default,
        };
        var count = 0;
        foreach (var comma in commas) {
            if (comma.SpanStart < position) {
                count++;
            }
        }
        return count;
    }
}
