using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        var byPath = new Dictionary<string, MetadataReference>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies()) {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) {
                continue;
            }
            byPath.TryAdd(assembly.Location, MetadataReference.CreateFromFile(assembly.Location));
        }

        // Only resolvable file-based references are valid in a fresh compilation;
        // by-name/unresolved ones (framework refs) are already covered above by
        // the loaded assemblies. Skipping them avoids
        // "UnresolvedMetadataReference is not valid for this compilation".
        foreach (var reference in snapshot.References) {
            if (reference is PortableExecutableReference pe && !string.IsNullOrEmpty(pe.FilePath)) {
                byPath.TryAdd(pe.FilePath, pe);
            }
        }

        return byPath.Values.ToArray();
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

        // Span of existing text being replaced, mapped back to cell coordinates.
        var replaceStart = Math.Max(0, completions.Span.Start - prefix);
        return new CompletionResultDto(replaceStart, completions.Span.Length, items);
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
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, pos, cancellationToken)
            .ConfigureAwait(false);
        if (symbol == null) {
            return empty;
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

        var sb = new StringBuilder();
        foreach (var section in info.Sections) {
            var text = section.Text;
            if (!string.IsNullOrEmpty(text)) {
                if (sb.Length > 0) {
                    sb.Append("\n\n");
                }
                sb.Append(text);
            }
        }

        var start = Math.Max(0, info.Span.Start - prefix);
        return new HoverDto(sb.ToString(), start, info.Span.Length);
    }

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
                Documentation: string.Empty,
                Parameters: m.Parameters.Select(p =>
                    new ParameterDto(p.ToDisplayString(_signatureFormat), string.Empty)).ToArray()))
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
