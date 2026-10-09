namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;

/// <summary>
/// Whether each physical side's LEDs run against the clockwise perimeter order
/// the rest of the pipeline assumes (top left to right, right top to bottom,
/// bottom right to left, left bottom to top).
/// </summary>
/// <remarks>
/// Until this existed, that one clockwise-from-the-top-left-corner assumption
/// was the only wiring the plugin could express. A real build rarely obliges:
/// a perimeter assembled from four cut runs joined by corner wires has an
/// independent data direction per side, and a side soldered the other way
/// round renders as that side's picture mirrored end for end while the other
/// three stay correct. Nothing about that is diagnosable from the colours
/// alone, and nothing in the configuration could correct it, so the only
/// remedy was to resolder the strip.
///
/// Reversal is applied to the physical LED run, after resampling, so it is
/// purely a wiring correction: the sampled picture geometry, the logical
/// sample counts, and every per-side colour trim are all untouched by it.
/// </remarks>
public readonly record struct LedSideDirections(
    bool ReverseTop = false,
    bool ReverseRight = false,
    bool ReverseBottom = false,
    bool ReverseLeft = false)
{
    /// <summary>Every side wired the way the clockwise perimeter order expects.</summary>
    public static LedSideDirections Clockwise => default;

    public bool IsClockwise => !ReverseTop && !ReverseRight && !ReverseBottom && !ReverseLeft;
}
