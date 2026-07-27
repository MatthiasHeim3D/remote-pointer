namespace RemotePointer.Contracts.Messages;

/// <summary>
/// A room is the plain name that scopes the host directory. Unlike the server password it is
/// not a secret: it travels in the clear, is stored in the preferences file, and is shown back
/// to the user so they can read out which room they are in. Everyone who reaches the relay has
/// already presented the server password, so a room separates teams rather than guarding them.
/// </summary>
public static class RoomName
{
    /// <summary>The room a client joins until it names another one.</summary>
    public const string Default = "general";

    public const int MaximumLength = 64;

    /// <summary>
    /// The canonical form two clients must agree on to share a room. Case is folded so that
    /// "Engineering" and "engineering" are one room, and each side keeps showing whatever its
    /// own user typed.
    /// </summary>
    public static string Normalize(string? room)
    {
        var trimmed = room?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !IsValid(trimmed))
        {
            return Default;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Whether a name can be used as typed. An empty name is not valid to enter, but it is
    /// accepted everywhere as "the default room" rather than rejected.
    /// </summary>
    public static bool IsValid(string? room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return false;
        }

        var trimmed = room.Trim();
        return trimmed.Length <= MaximumLength
            && !trimmed.Any(char.IsControl);
    }
}
