#pragma warning disable CA1848, CA1873
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Resolves the authoritative Jellyfin media source rather than using
/// BaseItem.Path, which can point at the wrong alternate version.
/// </summary>
public sealed class JellyfinFfmpegAnalysisSourceResolver : IFfmpegAnalysisSourceResolver
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<JellyfinFfmpegAnalysisSourceResolver> _logger;

    public JellyfinFfmpegAnalysisSourceResolver(
        IMediaSourceManager mediaSourceManager,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        ILogger<JellyfinFfmpegAnalysisSourceResolver> logger)
    {
        _mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FfmpegAnalysisSource?> ResolveAsync(PlaybackWorkerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.Source;
        if (reference is null || !Guid.TryParse(reference.ItemId, out var itemId) || string.IsNullOrWhiteSpace(reference.MediaSourceId))
        {
            _logger.LogWarning("Realtime Ambilight could not resolve playback source reference.");
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("Realtime Ambilight item {ItemId} was not found.", itemId);
            return null;
        }

        var mediaSource = await _mediaSourceManager.GetMediaSource(
            item,
            reference.MediaSourceId,
            reference.LiveStreamId,
            enablePathSubstitution: false,
            cancellationToken).ConfigureAwait(false);
        if (mediaSource is null || string.IsNullOrWhiteSpace(mediaSource.Path) || mediaSource.IsRemote)
        {
            _logger.LogWarning("Realtime Ambilight media source {MediaSourceId} was unavailable or remote.", reference.MediaSourceId);
            return null;
        }

        _logger.LogInformation("Realtime Ambilight resolved source {Path}.", mediaSource.Path);

        return new FfmpegAnalysisSource(
            _mediaEncoder.EncoderPath,
            mediaSource.Path,
            CreateFrameOptions());
    }

    private static AnalysisFrameOptions CreateFrameOptions()
    {
        var configuration = Plugin.Instance?.Configuration;
        return new AnalysisFrameOptions
        {
            Width = Math.Clamp(configuration?.AnalysisWidth ?? 160, 16, 1920),
            Height = Math.Clamp(configuration?.AnalysisHeight ?? 90, 16, 1080),
            FramesPerSecond = Math.Clamp(configuration?.AnalysisFramesPerSecond ?? 30, 1, 60),
        };
    }
}
