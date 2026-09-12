using System;
using System.IO;
using System.Linq;
using System.Text;
// global:: because this project's own namespace ends in FSharp and would shadow the compiler's.
using global::FSharp.Compiler.Diagnostics;
using Microsoft.FSharp.Core;
using static global::FSharp.Compiler.Interactive.Shell;

namespace ClrKernel.Language.FSharp;

/// <summary>An F# cell failed to compile or threw. The message is what fsi would print.</summary>
public sealed class FSharpCellException : Exception {
    public FSharpCellException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>
/// One F# Interactive session per notebook: a binding made in one cell is there in
/// the next, the same model as <c>#!pwsh</c> and <c>#!python</c>. Nothing flows
/// between it and the C# script state — the two are different compilers over
/// different sessions, and pretending otherwise would be a bug with a nice name.
/// <para>
/// Printed output goes to whatever <c>Console.Out</c> is <em>at the time of the
/// write</em>, which is the kernel's console proxy during a cell, so
/// <c>printfn</c> streams like <c>Console.WriteLine</c> does. Fsi captures its
/// writers once at creation, hence the forwarding writers rather than
/// <c>Console.Out</c> itself.
/// </para>
/// </summary>
public sealed class FSharpSession : IDisposable {
    private readonly Lazy<FsiEvaluationSession> _fsi;

    private readonly object _gate = new object();

    public FSharpSession() {
        _fsi = new Lazy<FsiEvaluationSession>(Create);
    }

    /// <summary>A bound value by name, from any earlier cell.</summary>
    public bool TryGetValue(string name, out object value) {
        lock (_gate) {
            var found = _fsi.Value.TryFindBoundValue(name);
            if (found == null || OptionModule.IsNone(found)) {
                value = null;
                return false;
            }
            value = found.Value.Value.ReflectionValue;
            return true;
        }
    }

    /// <summary>
    /// Binds a value under a name, as if a cell had declared it — how <c>#!share</c>
    /// lands. Not through <c>AddBoundValue(name, value)</c> directly: fsi
    /// reconstructs the F# type from the boxed value's runtime type, and in this
    /// host that type is not the one fsi's own references know — its <c>int</c>
    /// refuses <c>System.Int32</c>, and a <c>List&lt;string&gt;</c> will not unify with
    /// <c>seq</c>. So the value rides in an <c>obj[]</c> under a hidden name and a
    /// <c>let</c> unboxes it with the type spelled in source, which resolves through
    /// fsi's references and is a runtime cast on the way out.
    /// </summary>
    public void SetValue(string name, object value) {
        lock (_gate) {
            var carrier = "__clrkernel_share_" + name;
            _fsi.Value.AddBoundValue(carrier, new object[] { value });
            ExecuteCore($"let {name} : {FSharpTypeName(value?.GetType())} = unbox {carrier}.[0]");
        }
    }

    /// <summary>The F# spelling of a runtime type for a <c>let</c> annotation; <c>obj</c> when it has none a cell could write.</summary>
    public static string FSharpTypeName(Type type) {
        if (type == null) {
            return "obj";
        }
        if (type.IsArray) {
            return FSharpTypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }
        if (!type.IsVisible || type.IsGenericParameter || type.FullName == null) {
            return "obj";
        }
        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();
            var name = definition.FullName.Replace('+', '.');
            name = name.Substring(0, name.IndexOf('`'));
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FSharpTypeName)) + ">";
        }
        return type.FullName.Replace('+', '.');
    }

    /// <summary>Parses and type-checks a cell against the session without running it — completion, hover, diagnostics.</summary>
    public (global::FSharp.Compiler.CodeAnalysis.FSharpParseFileResults Parse, global::FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults Check) Check(string code) {
        lock (_gate) {
            var (parse, check, _) = _fsi.Value.ParseAndCheckInteraction(code ?? string.Empty, null);
            return (parse, check);
        }
    }

    private static FsiEvaluationSession Create() {
        var config = FsiEvaluationSession.GetDefaultConfiguration();
        // --noninteractive: no prompt, no stdin; --nologo/--quiet: the banner is not output.
        var args = new[] { "fsi.exe", "--noninteractive", "--nologo", "--quiet", "--gui-" };
        return FsiEvaluationSession.Create(
            config, args, TextReader.Null,
            new ForwardingWriter(() => Console.Out),
            new ForwardingWriter(() => Console.Error),
            FSharpOption<bool>.Some(false),
            null);
    }

    /// <summary>
    /// Evaluates the cell. Returns the trailing expression's value (null for a
    /// declaration or a unit), throws <see cref="FSharpCellException"/> with fsi's
    /// own diagnostics when it does not compile or raises.
    /// </summary>
    public object Execute(string code) {
        lock (_gate) {
            return ExecuteCore(code);
        }
    }

    private object ExecuteCore(string code) {
        var (result, diagnostics) = _fsi.Value.EvalInteractionNonThrowing(code ?? string.Empty, null);
        var errors = diagnostics.Where(d => d.Severity == FSharpDiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 || result.IsChoice2Of2) {
            var text = new StringBuilder();
            foreach (var error in errors) {
                text.AppendLine(Format(error));
            }
            if (result is FSharpChoice<FSharpOption<FsiValue>, Exception>.Choice2Of2 failed && errors.Count == 0) {
                text.AppendLine(failed.Item.Message);
            }
            var inner = result is FSharpChoice<FSharpOption<FsiValue>, Exception>.Choice2Of2 f ? f.Item : null;
            throw new FSharpCellException(text.ToString().TrimEnd(), inner);
        }
        foreach (var warning in diagnostics.Where(d => d.Severity == FSharpDiagnosticSeverity.Warning)) {
            Console.Error.WriteLine(Format(warning));
        }
        var value = ((FSharpChoice<FSharpOption<FsiValue>, Exception>.Choice1Of2)result).Item;
        if (value == null || OptionModule.IsNone(value)) {
            return null;
        }
        var reflected = value.Value.ReflectionValue;
        // A unit result is fsi's "nothing to show"; the kernel shows nothing too.
        return reflected is Unit ? null : reflected;
    }

    private static string Format(FSharpDiagnostic d) =>
        $"{d.Severity.ToString().ToLowerInvariant()} FS{d.ErrorNumber:D4}: {d.Message} (line {d.StartLine}, col {d.StartColumn + 1})";

    public void Dispose() {
        if (_fsi.IsValueCreated) {
            ((IDisposable)_fsi.Value).Dispose();
        }
    }

    private sealed class ForwardingWriter : TextWriter {
        private readonly Func<TextWriter> _target;
        public ForwardingWriter(Func<TextWriter> target) => _target = target;
        public override Encoding Encoding => _target().Encoding;
        public override void Write(char value) => _target().Write(value);
        public override void Write(string value) => _target().Write(value);
        public override void Write(char[] buffer, int index, int count) => _target().Write(buffer, index, count);
        public override void Flush() => _target().Flush();
    }
}
