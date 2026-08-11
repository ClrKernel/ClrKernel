using System;

namespace ClrKernel.Database;

/// <summary>Coerces database values (including <see cref="DBNull"/>) to CLR types.</summary>
internal static class ValueConverter {
    public static T To<T>(object value) => (T)To(typeof(T), value);

    public static object To(Type target, object value) {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (value is null || value is DBNull) {
            return underlying.IsValueType && Nullable.GetUnderlyingType(target) == null
                ? Activator.CreateInstance(underlying) // default(T) for non-nullable value types
                : null;
        }

        if (underlying.IsInstanceOfType(value)) {
            return value;
        }

        if (underlying.IsEnum) {
            return value is string s ? Enum.Parse(underlying, s, ignoreCase: true) : Enum.ToObject(underlying, value);
        }

        if (underlying == typeof(Guid)) {
            return value is Guid g ? g : Guid.Parse(value.ToString());
        }

        if (underlying == typeof(string)) {
            return value.ToString();
        }

        try {
            return Convert.ChangeType(value, underlying);
        } catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) {
            throw new InvalidCastException(
                $"Cannot convert value '{value}' ({value.GetType().Name}) to {underlying.Name}.", e);
        }
    }

    public static bool IsScalar(Type type) {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum
            || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid)
            || t == typeof(byte[]);
    }
}
