using System.Globalization;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>
/// Builds the pinned VAAPI/libplacebo HDR analysis graph. This is deliberately
/// separate from the SDR builder so unsupported HDR never degrades silently.
/// </summary>
public static class FfmpegHdrAnalysisCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(
        FfmpegAnalysisSource source,
        TimeSpan seekPosition,
        HdrVideoProfile profile,
        string devicePath = "/dev/dri/renderD128")
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(seekPosition, TimeSpan.Zero);

        if (string.IsNullOrWhiteSpace(devicePath) || !Path.IsPathFullyQualified(devicePath))
        {
            throw new ArgumentException("An absolute VAAPI device path is required.", nameof(devicePath));
        }

        var options = source.FrameOptions;
        var seekSeconds = seekPosition.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var applyDolbyVision = profile == HdrVideoProfile.DolbyVision ? "true" : "false";
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"scale_vaapi=w=960:h=540,hwdownload,format=p010le,hwupload,libplacebo=w={options.Width}:h={options.Height}:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:tonemapping=bt.2390:apply_dolbyvision={applyDolbyVision}:format=bgra,hwdownload,format=bgra");

        return
        [
            "-hide_banner", "-nostdin", "-loglevel", "warning",
            "-ss", seekSeconds,
            "-hwaccel", "vaapi",
            "-hwaccel_device", devicePath,
            "-hwaccel_output_format", "vaapi",
            "-re", "-i", source.MediaPath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", filter,
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1",
        ];
    }
}
