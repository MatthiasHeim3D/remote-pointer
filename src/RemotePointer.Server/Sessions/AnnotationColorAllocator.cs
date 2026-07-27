using RemotePointer.Contracts.Messages;

namespace RemotePointer.Server.Sessions;

/// <summary>
/// Decides what colour each annotator in a session actually draws in.
/// </summary>
/// <remarks>
/// <para>
/// An annotator asks for a colour; it gets that colour unless somebody ahead of it already holds
/// it, in which case it is moved to a free one from <see cref="AnnotationColors.Palette"/>. Only
/// the palette is handed out — a colour nobody asked for should still be one of the presets the
/// settings pane offers, so the annotator recognises it. A custom colour is honoured when it is
/// unclaimed, and only replaced when it clashes.
/// </para>
/// <para>
/// Priority is join order, so an annotator already drawing is never moved to make room for one
/// that just arrived. Because the whole session is reallocated from scratch on every change, an
/// annotator that was moved off its preference returns to it the moment the annotator holding it
/// leaves — no separate restore step, and no way for the two to drift apart.
/// </para>
/// <para>
/// Past <see cref="AnnotationColors.Palette"/>.Count annotators there is nothing distinct left to
/// give, so colours repeat, spread as evenly as the palette allows rather than piling onto one.
/// </para>
/// </remarks>
public static class AnnotationColorAllocator
{
    /// <summary>
    /// Allocates one colour per annotator, in join order.
    /// </summary>
    /// <param name="preferredColors">
    /// Each annotator's requested colour, ordered oldest join first. Null or malformed entries
    /// are read as <see cref="AnnotationColors.Default"/>.
    /// </param>
    public static string[] Allocate(IReadOnlyList<string?> preferredColors)
    {
        ArgumentNullException.ThrowIfNull(preferredColors);

        var allocated = new string[preferredColors.Count];
        var holders = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < preferredColors.Count; index++)
        {
            var preferred = AnnotationColors.Normalize(preferredColors[index]);
            var color = holders.ContainsKey(preferred) ? TakeLeastCrowded(holders) : preferred;
            allocated[index] = color;
            holders[color] = holders.GetValueOrDefault(color) + 1;
        }

        return allocated;
    }

    /// <summary>
    /// The palette colour with the fewest holders, earliest in palette order when several tie.
    /// Below capacity that is always an unused colour; above it, it is the one that spreads the
    /// repeats most evenly.
    /// </summary>
    private static string TakeLeastCrowded(Dictionary<string, int> holders)
    {
        var best = AnnotationColors.Palette[0];
        var bestCount = int.MaxValue;
        foreach (var candidate in AnnotationColors.Palette)
        {
            var count = holders.GetValueOrDefault(candidate);
            if (count < bestCount)
            {
                best = candidate;
                bestCount = count;
                if (count == 0)
                {
                    break;
                }
            }
        }

        return best;
    }
}
