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
    byte[] Encode(ReadOnlySpan<LinearRgb> linearFrame, Rgb24Encoding encoding);
}
