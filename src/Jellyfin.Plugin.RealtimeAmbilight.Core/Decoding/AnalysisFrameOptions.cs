namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>
/// Fixed geometry of a packed BGRA analysis frame produced by FFmpeg.
/// Sampling owns colour conversion; this type only defines the binary pipe
/// contract between FFmpeg and managed code.
/// </summary>
public sealed class AnalysisFrameOptions
{
    public const int BytesPerPixel = 4;

    public int Width { get; init; } = 160;

    public int Height { get; init; } = 90;

    public int FramesPerSecond { get; init; } = 30;

    public int ByteLength
    {
        get
        {
            Validate();
            return checked(Width * Height * BytesPerPixel);
        }
    }

    public void Validate()
    {
        if (Width is < 16 or > 1920)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "Analysis width must be between 16 and 1920 pixels.");
        }

        if (Height is < 16 or > 1080)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), "Analysis height must be between 16 and 1080 pixels.");
        }

        if (FramesPerSecond is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond), "Analysis frame rate must be between 1 and 60 frames per second.");
        }
    }
}
