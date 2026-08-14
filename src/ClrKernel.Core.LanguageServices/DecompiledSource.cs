using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.CodeAnalysis;
using FullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;

namespace ClrKernel.Core.LanguageServices;

/// <summary>
/// Turns a metadata symbol into readable C# with the ILSpy engine, so Go to
/// Definition works for anything referenced without source — the BCL, nuget
/// packages, and ClrKernel's own assemblies. The whole containing top-level type
/// is decompiled (a member alone loses its context) and the requested member is
/// located inside it for the initial selection.
/// </summary>
public static class DecompiledSource {
    public static async Task<MetadataSourceDto> ForSymbolAsync(
        Document document, ISymbol symbol, CancellationToken cancellationToken = default) {
        try {
            var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var assembly = symbol.ContainingAssembly;
            if (compilation == null || assembly == null) {
                return null;
            }
            if (compilation.GetMetadataReference(assembly) is not PortableExecutableReference pe
                || string.IsNullOrEmpty(pe.FilePath)) {
                return null;
            }

            // Decompile the OUTERMOST type: nested members read best in context.
            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (type == null) {
                return null;
            }
            while (type.ContainingType != null) {
                type = type.ContainingType;
            }
            var metadataName = type.ContainingNamespace is { IsGlobalNamespace: false }
                ? type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName
                : type.MetadataName;

            var settings = new DecompilerSettings {
                ThrowOnAssemblyResolveErrors = false,
                ShowXmlDocumentation = true,
            };
            var decompiler = new CSharpDecompiler(pe.FilePath, settings);
            var text = decompiler.DecompileTypeAsString(new FullTypeName(metadataName));

            var (start, length) = LocateMember(text, symbol);
            return new MetadataSourceDto(KeyFor(pe.FilePath, metadataName), text, start, length);
        } catch (Exception e) {
            // Best-effort: "no definition" beats a broken peek — but say why on
            // stderr, which is the log channel in every host mode.
            Console.Error.WriteLine(
                $"clrkernel: decompilation failed for {symbol.ToDisplayString()}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // A stable, URI-path-safe name ending in .cs so editors pick C# highlighting.
    private static string KeyFor(string assemblyPath, string typeName) {
        var safe = new string(typeName.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(assemblyPath + "|" + typeName)));
        return safe + "-" + hash.Substring(0, 8) + ".cs";
    }

    // Best-effort: the first occurrence of the member's name that reads like a
    // declaration (not preceded by a dot or identifier character). Falls back to
    // the top of the document.
    internal static (int Start, int Length) LocateMember(string text, ISymbol symbol) {
        var name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? symbol.ContainingType.Name : symbol.Name;
        if (string.IsNullOrEmpty(name)) {
            return (0, 0);
        }
        var index = 0;
        while ((index = text.IndexOf(name, index, StringComparison.Ordinal)) >= 0) {
            var before = index > 0 ? text[index - 1] : ' ';
            var afterIndex = index + name.Length;
            var after = afterIndex < text.Length ? text[afterIndex] : ' ';
            var declarationish = before != '.' && !char.IsLetterOrDigit(before) && before != '_'
                && !char.IsLetterOrDigit(after) && after != '_';
            if (declarationish) {
                return (index, name.Length);
            }
            index += name.Length;
        }
        return (0, 0);
    }
}
