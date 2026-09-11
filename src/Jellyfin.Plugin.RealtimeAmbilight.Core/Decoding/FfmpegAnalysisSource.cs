namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>
/// Per-session source information already resolved by the Jellyfin adapter.
/// It intentionally contains paths, not a shell command: all values become
/// individual <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> entries.
/// </summary>
public sealed record FfmpegAnalysisSource(
    string EncoderPath,
    string MediaPath,
    AnalysisFrameOptions FrameOptions,
    HdrVideoProfile? VideoProfile = null,
    string HardwareDevicePath = "/dev/dri/renderD128",
    double? SourceFramesPerSecond = null)
{
    public void Validate()
    {
        ValidateAbsolutePath(EncoderPath, nameof(EncoderPath));
        ValidateAbsolutePath(MediaPath, nameof(MediaPath));
        ArgumentNullException.ThrowIfNull(FrameOptions);
        FrameOptions.Validate();
        if (VideoProfile.HasValue && (string.IsNullOrWhiteSpace(HardwareDevicePath) || !Path.IsPathFullyQualified(HardwareDevicePath)))
        {
            throw new ArgumentException("An absolute hardware device path is required for HDR analysis.", nameof(HardwareDevicePath));
        }
    }

    private static void ValidateAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute local path is required.", parameterName);
        }
    }
}
