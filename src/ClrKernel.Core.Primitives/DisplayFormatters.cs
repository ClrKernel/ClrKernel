using System;
using System.Collections.Generic;

namespace ClrKernel.Core.Primitives;

/// <summary>One registered conversion between two display concepts.</summary>
public record DisplayFormatter(Type InputType, Type OutputType, Func<IDisplayValue, IDisplayValue> Format);

/// <summary>
/// The formatter registry: every conversion between display concepts — including every
/// render, since a render is just a conversion to <see cref="DisplayHtml"/> or
/// <see cref="DisplayText"/> — is looked up here. Primitives itself registers only
/// concept-level fallbacks (ToString); rendering packages (ClrKernel.Formatting.*) and
/// user code register the rest. The newest matching registration wins, so overriding a
/// built-in is just registering after it.
/// </summary>
public static class DisplayFormatters {
    private static readonly object _gate = new object();
    private static readonly List<DisplayFormatter> _formatters = new List<DisplayFormatter>();

    static DisplayFormatters() {
        // Concept-level fallbacks only — nothing here renders. Running in the static
        // ctor guarantees they precede (and so lose to) every external registration.
        Register<DisplayObject, DisplayText>(o => new DisplayText(o.Value?.ToString() ?? ""));
        Register<DisplayObject, DisplayTable>(TableExtractor.Extract);
        Register<DisplayConsoleText, DisplayText>(c => new DisplayText(c.ConsoleOutput ?? ""));
        Register<DisplayBadge, DisplayText>(b => new DisplayText(b.Label + ": " + b.Text));
        Register<DisplayProgress, DisplayText>(p => {
            var status = !string.IsNullOrEmpty(p.Status) ? p.Status
                : p.Total > 0 ? $"{p.Completed:0.#} / {p.Total:0.#}"
                : p.Completed.ToString("0.#");
            return new DisplayText(string.IsNullOrEmpty(p.Label) ? status : p.Label + " · " + status);
        });
    }

    public static DisplayHtml ToHtml(this IDisplayValue value) => Format<DisplayHtml>(value);

    public static DisplayText ToText(this IDisplayValue value) => Format<DisplayText>(value);

    public static T Format<T>(IDisplayValue value) where T : IDisplayValue {
        if (TryFormat<T>(value, out var formatted)) {
            return formatted;
        }
        throw new InvalidOperationException(
            $"No formatter converts {value?.GetType().Name ?? "null"} to {typeof(T).Name}. Register one with DisplayFormatters.Register.");
    }

    public static bool TryFormat<T>(IDisplayValue value, out T formatted) where T : IDisplayValue {
        if (value != null) {
            value = Resolve(value);
        }
        if (value is T already) {
            formatted = already;
            return true;
        }
        var formatter = value == null ? null : Find(value.GetType(), typeof(T));
        if (formatter == null) {
            formatted = default;
            return false;
        }
        formatted = (T)formatter.Format(value);
        return true;
    }

    /// <summary>
    /// Honours a <see cref="DisplayObject.PreferredDisplayType"/> before any other lookup:
    /// the raw value is coerced (string-like concepts) or converted (via the registry) to
    /// the concept the caller asked for. Anything else passes through unchanged.
    /// </summary>
    public static IDisplayValue Resolve(IDisplayValue value) {
        if (!(value is DisplayObject obj) || obj.PreferredDisplayType == null) {
            return value;
        }
        var coerced = Coerce(obj);
        if (coerced != null) {
            return coerced;
        }
        var toPreferred = Find(obj.GetType(), obj.PreferredDisplayType);
        if (toPreferred != null) {
            var converted = toPreferred.Format(obj);
            if (!(converted is DisplayObject)) {
                return converted;
            }
        }
        return value; // preference unsatisfiable — fall back to the raw object
    }

    // A preference for a string-like concept means "my value IS that concept", not
    // "render my value as it": "<b>x</b>".DisplayHtml() shows bold, never markup-escaped.
    private static IDisplayValue Coerce(DisplayObject obj) {
        var type = obj.PreferredDisplayType;
        if (obj.Value is IDisplayValue nested && type.IsInstanceOfType(nested)) {
            return nested;
        }
        var text = obj.Value?.ToString() ?? "";
        if (type == typeof(DisplayText)) {
            return new DisplayText(text);
        }
        if (type == typeof(DisplayHtml)) {
            return new DisplayHtml(text);
        }
        if (type == typeof(DisplayMarkdown)) {
            return new DisplayMarkdown(text);
        }
        if (type == typeof(DisplayConsoleText)) {
            return new DisplayConsoleText(text);
        }
        if (type == typeof(DisplayBytes)) {
            return obj.Value is byte[] bytes ? new DisplayBytes(bytes, obj.PreferredMimeType) : null;
        }
        return null; // structural concepts (DisplayTable, ...) need a registered formatter
    }

    private static DisplayFormatter Find(Type inputType, Type outputType) {
        DisplayFormatter[] snapshot;
        lock (_gate) {
            snapshot = _formatters.ToArray();
        }
        // Newest first, so the latest registration overrides earlier ones.
        for (var i = snapshot.Length - 1; i >= 0; i--) {
            if (snapshot[i].InputType.IsAssignableFrom(inputType) && snapshot[i].OutputType == outputType) {
                return snapshot[i];
            }
        }
        // One intermediate hop: input -> mid -> output.
        for (var i = snapshot.Length - 1; i >= 0; i--) {
            var first = snapshot[i];
            if (!first.InputType.IsAssignableFrom(inputType)) {
                continue;
            }
            for (var j = snapshot.Length - 1; j >= 0; j--) {
                var second = snapshot[j];
                if (second.OutputType == outputType && second.InputType.IsAssignableFrom(first.OutputType)) {
                    return new DisplayFormatter(inputType, outputType, v => second.Format(first.Format(v)));
                }
            }
        }
        return null;
    }

    public static DisplayFormatter Register<TIn, TOut>(Func<TIn, TOut> format)
        where TIn : IDisplayValue
        where TOut : IDisplayValue =>
        Register(typeof(TIn), typeof(TOut), value => format((TIn)value));

    public static DisplayFormatter Register(Type inputType, Type outputType, Func<IDisplayValue, IDisplayValue> format) {
        var formatter = new DisplayFormatter(inputType, outputType, format);
        lock (_gate) {
            _formatters.Add(formatter);
        }
        return formatter;
    }

    public static bool Unregister(DisplayFormatter formatter) {
        if (formatter == null) {
            return false;
        }
        lock (_gate) {
            return _formatters.Remove(formatter);
        }
    }

    public static bool Unregister<TIn, TOut>()
        where TIn : IDisplayValue
        where TOut : IDisplayValue =>
        Unregister(typeof(TIn), typeof(TOut));

    public static bool Unregister(Type inputType, Type outputType) {
        lock (_gate) {
            var found = _formatters.FindLast(f => f.InputType == inputType && f.OutputType == outputType);
            return found != null && _formatters.Remove(found);
        }
    }

    public static IEnumerable<DisplayFormatter> Formatters {
        get {
            lock (_gate) {
                return _formatters.ToArray();
            }
        }
    }
}
