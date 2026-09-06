namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>A half-open pixel rectangle used for one logical edge sample.</summary>
public readonly record struct SamplingZone(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
