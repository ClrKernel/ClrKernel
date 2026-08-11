using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;

namespace ClrKernel.Database;

/// <summary>
/// Binds a parameter object to a command: an <see cref="IDictionary{TKey,TValue}"/>
/// (keyed by name) or any object whose public properties become <c>@name</c>
/// parameters — e.g. <c>db.Query("... where Id = @id", new { id = 5 })</c>.
/// </summary>
internal static class ParameterBinder {
    public static void Bind(IDbCommand command, object parameters) {
        if (parameters is null) {
            return;
        }

        if (parameters is IDictionary<string, object> map) {
            foreach (var pair in map) {
                Add(command, pair.Key, pair.Value);
            }
            return;
        }

        foreach (var property in parameters.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) {
                Add(command, property.Name, property.GetValue(parameters));
            }
        }
    }

    private static void Add(IDbCommand command, string name, object value) {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name.StartsWith("@", StringComparison.Ordinal) ? name : "@" + name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
