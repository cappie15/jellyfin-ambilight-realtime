using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

/// <summary>Supplies the media path and encoder selected for one playback request.</summary>
public interface IFfmpegAnalysisSourceResolver
{
    Task<FfmpegAnalysisSource?> ResolveAsync(PlaybackWorkerRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one independently-owned FFmpeg child process and places complete BGRA
/// analysis frames in the latest-only buffer. Cancellation kills the whole child
/// process tree and is awaited before the worker completes.
/// </summary>
public sealed class FfmpegAnalysisWorker : IPlaybackAnalysisWorker
{
    private const int MaximumCapturedErrorCharacters = 16 * 1024;
    private readonly IFfmpegAnalysisSourceResolver _sourceResolver;

    public FfmpegAnalysisWorker(IFfmpegAnalysisSourceResolver sourceResolver)
    {
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    }

    public async Task RunAsync(
        PlaybackWorkerRequest request,
        LatestFrameBuffer<AnalysisFrame> latestFrames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(latestFrames);

        var source = await _sourceResolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No FFmpeg analysis source was resolved for the playback request.");
        var startInfo = source.VideoProfile is { } profile
            ? CreateHdrStartInfo(source, request, profile)
            : FfmpegAnalysisCommandBuilder.CreateSdrStartInfo(source, TimeSpan.FromTicks(request.PositionTicks));
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg did not start an analysis process.");
        }

        var standardError = DrainStandardErrorAsync(process.StandardError);
        try
        {
            var frameLength = source.FrameOptions.ByteLength;
            while (await ReadCompleteFrameAsync(
                process.StandardOutput.BaseStream,
                frameLength,
                source.FrameOptions.Width,
                source.FrameOptions.Height,
                latestFrames,
                cancellationToken).ConfigureAwait(false))
            {
                // Publishing replaces a stale frame rather than accumulating latency.
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var errorText = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg analysis exited with code {process.ExitCode}: {errorText}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await standardError.ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateHdrStartInfo(
        FfmpegAnalysisSource source,
        PlaybackWorkerRequest request,
        HdrVideoProfile profile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = source.EncoderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in FfmpegHdrAnalysisCommandBuilder.BuildArguments(
            source, TimeSpan.FromTicks(request.PositionTicks), profile, source.HardwareDevicePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<bool> ReadCompleteFrameAsync(
        Stream standardOutput,
        int frameLength,
        int frameWidth,
        int frameHeight,
        LatestFrameBuffer<AnalysisFrame> latestFrames,
        CancellationToken cancellationToken)
    {
        var frame = GC.AllocateUninitializedArray<byte>(frameLength);
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await standardOutput.ReadAsync(frame.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        latestFrames.Publish(new AnalysisFrame(frame, frameWidth, frameHeight));
        return true;
    }

    private static async Task<string> DrainStandardErrorAsync(StreamReader standardError)
    {
        var captured = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await standardError.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return captured.ToString();
            }

            var remainingCapacity = MaximumCapturedErrorCharacters - captured.Length;
            if (remainingCapacity > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, remainingCapacity));
            }
        }
    }
}
