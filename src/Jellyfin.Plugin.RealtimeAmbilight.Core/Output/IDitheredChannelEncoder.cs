using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Encodes a linear-light physical frame onto the wire, carrying quantization
/// error forward between calls. Implemented once per wire layout -- three
/// bytes per LED (<see cref="DitheredRgb24Encoder"/>) or four
/// (<see cref="DitheredRgbw32Encoder"/>) -- so callers that do not care which
/// strip is attached can hold just this.
/// </summary>
public interface IDitheredChannelEncoder
{
    /// <param name="whiteExtractionFactor">
    /// RGBW32-only: how much of a pixel's shared grey (<c>min(r,g,b)</c>)
    /// actually goes to the physical white LED, 0-1, defaulting to 1 (extract
    /// all of it -- today's behaviour, and RGB24 ignores this entirely).
    /// Driven by how far the White step's own colour-temperature control is
    /// currently set from centre (see <c>PerimeterColourAdjustment.WhiteExtractionFactor</c>),
    /// not by any one pixel's own saturation, so ordinary saturated video
    /// content is never affected by it -- only the frame's global white
    /// balance is. See <see cref="DitheredRgbw32Encoder"/> for why this
    /// exists.
    /// </param>
    byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding, float whiteExtractionFactor = 1f);
}
