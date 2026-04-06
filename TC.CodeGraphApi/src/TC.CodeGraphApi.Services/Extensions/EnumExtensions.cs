namespace TC.CodeGraphApi.Services.Extensions;

public static class EnumExtensions
{
    /// <summary>
    /// Try to parse a string into an enum value, returning null on failure.
    /// Case-insensitive by default.
    /// </summary>
    public static T? TryParseEnum<T>(this string? value, bool ignoreCase = true)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse<T>(value, ignoreCase, out var result) ? result : null;
    }
}
