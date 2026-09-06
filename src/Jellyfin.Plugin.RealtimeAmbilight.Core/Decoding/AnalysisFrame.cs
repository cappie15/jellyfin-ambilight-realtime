namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>
/// One complete packed BGRA frame read from the analysis decoder. The byte array
/// is exclusively owned by the latest-frame pipeline after construction.
/// </summary>
public sealed class AnalysisFrame
{
    public AnalysisFrame(byte[] bgraPixels, int width, int height, long positionTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width < 16 || height < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Analysis frames must be at least 16 by 16 pixels.");
        }

        var expectedLength = checked(width * height * AnalysisFrameOptions.BytesPerPixel);
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} BGRA bytes but received {bgraPixels.Length}.", nameof(bgraPixels));
        }

        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
        PositionTicks = positionTicks;
    }

    public byte[] BgraPixels { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Position in the media timeline that this frame depicts. Output schedules
    /// against it rather than against arrival time, so a decoder that runs early
    /// or late does not shift the LEDs away from the picture.
    /// </summary>
    public long PositionTicks { get; }
}
