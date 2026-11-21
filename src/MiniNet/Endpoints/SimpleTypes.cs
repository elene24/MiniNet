using System.ComponentModel;
using System.Globalization;

namespace MiniNet.Endpoints;

/// <summary>Types that can be read from a single string: route values, query values and headers.</summary>
internal static class SimpleTypes
{
    public static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum ||
               type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
               type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ||
               type == typeof(TimeOnly) || type == typeof(TimeSpan);
    }

    public static bool TryParse(string value, Type type, out object? result)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var culture = CultureInfo.InvariantCulture;
        result = null;

        if (target == typeof(string))
        {
            result = value;
            return true;
        }

        if (target.IsEnum)
            return Enum.TryParse(target, value, ignoreCase: true, out result) && Enum.IsDefined(target, result!);

        // The common types, parsed with the invariant culture so "1.5" means the same on every server.
        (bool ok, object? parsed) attempt = target switch
        {
            _ when target == typeof(int) => (int.TryParse(value, NumberStyles.Integer, culture, out var v), v),
            _ when target == typeof(long) => (long.TryParse(value, NumberStyles.Integer, culture, out var v), v),
            _ when target == typeof(bool) => (bool.TryParse(value, out var v), v),
            _ when target == typeof(double) => (double.TryParse(value, NumberStyles.Float, culture, out var v), v),
            _ when target == typeof(decimal) => (decimal.TryParse(value, NumberStyles.Number, culture, out var v), v),
            _ when target == typeof(Guid) => (Guid.TryParse(value, out var v), v),
            _ when target == typeof(DateOnly) => (DateOnly.TryParse(value, culture, DateTimeStyles.None, out var v), v),
            _ when target == typeof(TimeOnly) => (TimeOnly.TryParse(value, culture, DateTimeStyles.None, out var v), v),
            _ when target == typeof(DateTime) => (DateTime.TryParse(value, culture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v), v),
            _ when target == typeof(DateTimeOffset) => (DateTimeOffset.TryParse(value, culture, DateTimeStyles.AssumeUniversal, out var v), v),
            _ => (false, null),
        };

        if (attempt.ok)
        {
            result = attempt.parsed;
            return true;
        }

        if (attempt.parsed is not null) return false; // a known type that failed to parse

        // Everything else (byte, short, float, TimeSpan...) goes through the type's converter.
        try
        {
            result = TypeDescriptor.GetConverter(target).ConvertFromInvariantString(value);
            return result is not null;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
