#pragma warning disable CA1848, CA1873
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RealtimeAmbilight;

/// <summary>
/// Maps Jellyfin's own <see cref="VideoRangeType"/> classification to the
/// <see cref="HdrVideoProfile"/> the explicit hardware analysis graph needs
/// (or <see langword="null"/> for the plain SDR graph), per the table in
/// ADR-006. Kept as its own pure, static mapping -- rather than inline in
/// <see cref="JellyfinFfmpegAnalysisSourceResolver"/> -- so the classification
/// itself is testable without a real <c>MediaStream</c>/<c>MediaSourceManager</c>.
/// </summary>
/// <remarks>
/// Found and fixed 2026-09-14: <see cref="JellyfinFfmpegAnalysisSourceResolver"/>
/// never actually called this (or anything like it) before this class existed --
/// <c>FfmpegAnalysisSource.VideoProfile</c> was always left at its default
/// <see langword="null"/>, so every source, including genuine Dolby Vision,
/// silently went through the plain SDR analysis graph regardless of its real
/// range. ADR-006's entire HDR/DV command-building path
/// (<see cref="FfmpegHdrAnalysisCommandBuilder"/>) was fully implemented and
/// unit-tested in isolation, but never actually reachable from real playback --
/// exactly the "naive path" the ADR itself documents as producing a
/// green/cyan cast on genuine Dolby Vision. Reported live: an HDR10 title
/// looked fine (the SDR graph's own YUV->RGB conversion happens to read as
/// plausible for typical HDR10 content), a Dolby Vision title's colours were
/// "completely fucked up".
/// </remarks>
public static class HdrVideoProfileMapper
{
    public static HdrVideoProfile? Resolve(VideoRangeType videoRangeType, ILogger logger)
        => videoRangeType switch
        {
            // HDR10Plus's dynamic metadata is ignored, matching the base
            // HDR10 handling -- documented in ADR-006, not silently dropped.
            // The three DOVI-with-a-usable-base-layer shapes tone-map that
            // base layer and ignore the RPU/enhancement layer entirely, per
            // ADR-006's own table -- they are not the hard case below.
            VideoRangeType.HDR10 or VideoRangeType.HDR10Plus
                or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHDR10Plus
                or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithELHDR10Plus
                => HdrVideoProfile.Hdr10,
            VideoRangeType.HLG or VideoRangeType.DOVIWithHLG => HdrVideoProfile.Hlg,
            // The one Dolby Vision shape with no SDR/HDR10-compatible base
            // layer (profile 5 and similar): decoding it without applying the
            // RPU produces the well-known green/purple cast, so this is the
            // only case that actually needs apply_dolbyvision=true.
            VideoRangeType.DOVI => HdrVideoProfile.DolbyVision,
            VideoRangeType.DOVIInvalid => LogFallbackToHdr10(logger, videoRangeType),
            // DOVIWithSDR (ADR-006: "treat base as SDR"), plain SDR, Unknown,
            // and anything future/unrecognised: the plain SDR graph is the
            // safe default here -- forcing the HDR graph onto a source that
            // is not actually HDR/DV would apply an unwanted tone-map to an
            // otherwise ordinary picture.
            _ => null,
        };

    private static HdrVideoProfile? LogFallbackToHdr10(ILogger logger, VideoRangeType videoRangeType)
    {
        logger.LogWarning(
            "Realtime Ambilight could not classify a {VideoRangeType} source as valid Dolby Vision; falling back to HDR10 handling.",
            videoRangeType);
        return HdrVideoProfile.Hdr10;
    }
}
