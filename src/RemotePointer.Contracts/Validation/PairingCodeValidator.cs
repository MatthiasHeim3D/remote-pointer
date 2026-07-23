namespace RemotePointer.Contracts.Validation;

public static class PairingCodeValidator
{
    public const int CodeLength = 6;

    private const string AllowedCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Normalize(string? pairingCode) =>
        string.Concat((pairingCode ?? string.Empty)
            .Where(character => character is not '-' && !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    public static bool IsValid(string? pairingCode)
    {
        var normalized = Normalize(pairingCode);
        return normalized.Length == CodeLength
            && normalized.All(AllowedCharacters.Contains);
    }
}
