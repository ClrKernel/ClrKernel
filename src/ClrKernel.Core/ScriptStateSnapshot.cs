using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace ClrKernel.Core;

/// <summary>
/// Immutable snapshot of an <see cref="InteractiveScriptEngine"/>'s accumulated
/// script context: the resolved metadata references, the imported namespaces,
/// and the ordered prior submissions (the initial usings preamble followed by
/// each executed cell). Language services rebuild an equivalent Roslyn script
/// document from this so completion, hover, and signature help reflect the live
/// session — prior-cell symbols, <c>#r "nuget:"</c> types, and imports included.
/// </summary>
/// <param name="References">Resolved metadata references (BCL, ClrKernel, resolved nuget).</param>
/// <param name="Imports">Imported namespaces (compilation-level usings).</param>
/// <param name="Submissions">Ordered, successfully-executed cell code.</param>
/// <param name="Preamble">Always-present using directives (the cell-helper
/// using-statics) so completion offers them before the first cell runs.</param>
public sealed record ScriptStateSnapshot(
    IReadOnlyList<MetadataReference> References,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> Submissions,
    string Preamble);
