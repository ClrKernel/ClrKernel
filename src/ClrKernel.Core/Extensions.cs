using System.Collections.Generic;
using ClrKernel.Primitives;

namespace ClrKernel.Core;

public static class Extensions {
    public static DisplayData HTML(string html) {
        return new DisplayData {
            Data = new Dictionary<string, object>
            {
                { "text/html", html }
            }
        };
    }

    /// <summary>
    /// Returns the value of a notebook variable defined by an earlier cell
    /// (including parameter cells injected by papermill / dotnet-repl), or null
    /// when it does not exist.
    /// </summary>
    public static object GetVariable(string variable) {
        return InteractiveScriptEngine.Current?.GetVariableValue(variable);
    }

    /// <summary>
    /// Returns the value of a notebook variable, or <paramref name="defaultValue"/>
    /// when it is not defined — the standard pattern for parameterized job
    /// notebooks: <c>GetVariable("dateRanges", "0 to 1 months ago")</c>.
    /// </summary>
    public static T GetVariable<T>(string variable, T defaultValue) {
        return GetVariable(variable) switch {
            null => defaultValue,
            var value => (T)value,
        };
    }
}
