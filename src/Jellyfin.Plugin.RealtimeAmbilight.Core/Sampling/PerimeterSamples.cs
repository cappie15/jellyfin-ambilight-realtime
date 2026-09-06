using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>Logical edge colours in clockwise top, right, bottom, left order.</summary>
public sealed record PerimeterSamples(
    LinearRgb[] Top,
    LinearRgb[] Right,
    LinearRgb[] Bottom,
    LinearRgb[] Left);
