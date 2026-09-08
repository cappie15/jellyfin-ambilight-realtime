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

    /// <summary>
    /// The SDR analysis graph, decoded on the GPU instead of the CPU. This is
    /// the SDR sibling of <see cref="FfmpegHdrAnalysisCommandBuilder"/>, which
    /// already does this for HDR content -- the SDR path had simply never had
    /// the same treatment.
    /// </summary>
    /// <remarks>
    /// Measured live, decoding 8 s of a 2160p HEVC source on the reference
    /// host: software (<see cref="BuildSdrArguments"/>) took 184% CPU and
    /// 25 s wall-clock -- already 3x slower than real time on its own, before
    /// any other load on the host. The same decode via VAAPI took 68% CPU
    /// and 1.7 s -- comfortably faster than real time. A software-only
    /// analysis decode of 4K content was already marginal on a modest host
    /// before anything else competed for CPU; adding Hue Entertainment's own
    /// sustained processing was enough to push it over into the analysis
    /// decoder visibly, repeatedly falling behind and restarting -- this is
    /// the actual fix, not a change to either output path's own colour code.
    /// </remarks>
    public static IReadOnlyList<string> BuildVaapiSdrArguments(FfmpegAnalysisSource source, TimeSpan seekPosition, string devicePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        if (seekPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(seekPosition), "A decoder seek cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(devicePath) || !Path.IsPathFullyQualified(devicePath))
        {
            throw new ArgumentException("An absolute VAAPI device path is required.", nameof(devicePath));
        }

        var options = source.FrameOptions;
        var seekSeconds = seekPosition.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        // scale_vaapi resizes on the GPU; hwdownload brings the result back to
        // system memory as nv12 (the format VAAPI surfaces actually use), and
        // the final format=bgra is then a cheap software colour-space
        // conversion on an already-320x180 frame, not a decode.
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"fps={options.FramesPerSecond},scale_vaapi=w={options.Width}:h={options.Height},hwdownload,format=nv12,format=bgra");

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
