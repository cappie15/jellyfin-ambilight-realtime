namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Mapping;

/// <summary>
/// How much of the room's own coordinate space the television actually
/// occupies, so a light's position can be read against the screen rather than
/// against the walls.
/// </summary>
/// <remarks>
/// The bridge reports every position on a -1 to +1 room scale, and
/// <see cref="HueChannelMapper"/> originally fed those straight in as if -1
/// and +1 were the picture's own edges. A television does not span its wall.
/// Measured on the operator's own bridge, the three gradient strips that hug
/// the set report X and Z of only -0.4 to +0.4, so every one of them sampled
/// no more than the middle 40% of the picture: the left and right extremes of
/// the image never reached the strip under the set at all, and its colour
/// boundary landed at 70% of its length instead of halfway. Dividing by the
/// screen's own half-extent first puts a light at the edge of the screen at
/// the edge of the picture, which is what every threshold downstream already
/// assumed it was getting.
///
/// <see langword="default"/> is the previous behaviour (the screen treated as
/// filling the room), so an installation that has never set this is unchanged.
/// </remarks>
public readonly record struct HueScreenExtent(double HalfWidth = 1d, double HalfHeight = 1d)
{
    /// <summary>The screen treated as spanning the whole room, which is what this mapping assumed before the extent existed.</summary>
    public static HueScreenExtent Full => new(1d, 1d);

    /// <summary>
    /// Half-extents are clamped into a sane band, and <c>0</c> (what
    /// <see langword="default"/> gives) reads as the full room rather than as
    /// a division by zero. The floor of 0.1 stops a mistyped value turning
    /// every light into a hard left or right extreme.
    /// </summary>
    public double EffectiveHalfWidth => Normalise(HalfWidth);

    /// <inheritdoc cref="EffectiveHalfWidth"/>
    public double EffectiveHalfHeight => Normalise(HalfHeight);

    private static double Normalise(double value)
        => value <= 0d ? 1d : Math.Clamp(value, 0.1d, 1d);
}
