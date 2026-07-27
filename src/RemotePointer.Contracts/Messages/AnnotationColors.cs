namespace RemotePointer.Contracts.Messages;

/// <summary>
/// The wire format for the colour an annotator draws in. Both ends agree on it here so the host
/// can render an arriving event in the annotator's colour without guessing at the encoding.
/// </summary>
public static class AnnotationColors
{
    /// <summary>
    /// The colour used when an event carries none. It is the accent the client drew everything
    /// in before the colour was selectable, so an unset colour keeps looking the way it did.
    /// </summary>
    public const string Default = "#FF5C5C";

    /// <summary>Length of a valid value: a hash followed by six hexadecimal digits.</summary>
    public const int Length = 7;

    /// <summary>
    /// Whether <paramref name="color"/> is exactly <c>#RRGGBB</c> in upper case. Alpha is not
    /// part of the format: opacity is a local viewing preference on each end rather than
    /// something the annotator dictates.
    /// </summary>
    public static bool IsValid(string? color)
    {
        if (color is null || color.Length != Length || color[0] != '#')
        {
            return false;
        }

        for (var index = 1; index < Length; index++)
        {
            if (!IsUpperCaseHexDigit(color[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <paramref name="color"/> in canonical form, or <see cref="Default"/> when it is
    /// missing or malformed. Anything read from a settings file or off the wire goes through
    /// here, so a hand-edited or hostile value degrades to the default rather than propagating.
    /// </summary>
    public static string Normalize(string? color)
    {
        if (color is null)
        {
            return Default;
        }

        var trimmed = color.Trim().ToUpperInvariant();
        return IsValid(trimmed) ? trimmed : Default;
    }

    private static bool IsUpperCaseHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'F';
}
