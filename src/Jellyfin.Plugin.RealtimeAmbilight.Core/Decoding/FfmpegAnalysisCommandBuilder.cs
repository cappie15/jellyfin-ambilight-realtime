using System.Diagnostics;
using System.Globalization;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>
/// Creates the narrowly-scoped SDR analysis invocation. HDR/Dolby Vision and
/// hardware graphs are separate, explicitly tested paths and must not fall back
/// to this command silently.
/// </summary>
public static class FfmpegAnalysisCommandBuilder
{
    public static ProcessStartInfo CreateSdrStartInfo(FfmpegAnalysisSource source, TimeSpan seekPosition)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        if (seekPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(seekPosition), "A decoder seek cannot be negative.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = source.EncoderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildSdrArguments(source, seekPosition))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static IReadOnlyList<string> BuildSdrArguments(FfmpegAnalysisSource source, TimeSpan seekPosition)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        if (seekPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(seekPosition), "A decoder seek cannot be negative.");
        }

        var options = source.FrameOptions;
        var seekSeconds = seekPosition.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"fps={options.FramesPerSecond},scale={options.Width}:{options.Height}:flags=bilinear,format=bgra");

        return
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "warning",
            // Input options must precede -i. Output-side -ss can decode an entire
            // film before yielding a frame and looks indistinguishable from a hang.
            "-ss",
            seekSeconds,
            // Keep the independent analysis decoder on the playback timeline; it
            // must not race to EOF and overwrite the one-slot frame handoff.
            "-re",
            "-i",
            source.MediaPath,
            "-map",
            "0:v:0",
            "-an",
            "-sn",
            "-dn",
            "-vf",
            filter,
            "-pix_fmt",
            "bgra",
            "-f",
            "rawvideo",
            "pipe:1",
        ];
    }
}
