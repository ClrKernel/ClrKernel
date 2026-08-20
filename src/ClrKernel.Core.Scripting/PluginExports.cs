using System;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// Marks an assembly as exporting a cell language. When a session loads the
/// assembly (<c>#r "nuget: …"</c> or <c>#r "path.dll"</c>), the engine
/// instantiates the type and registers it with THAT session's language set —
/// selectors, language tags, directives, completions and all — without touching
/// other notebooks' engines. The shipped Language.* assemblies carry this too;
/// a language whose Id is already registered is skipped, so re-referencing a
/// built-in is a no-op.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CellLanguageExportAttribute : Attribute {
    /// <param name="languageType">An <see cref="ICellLanguage"/> with a parameterless constructor.</param>
    public CellLanguageExportAttribute(Type languageType) {
        LanguageType = languageType;
    }

    public Type LanguageType { get; }
}
