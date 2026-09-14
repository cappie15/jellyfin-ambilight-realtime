using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using HdrVideoProfileMapper = Jellyfin.Plugin.RealtimeAmbilight.HdrVideoProfileMapper;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Decoding;

/// <summary>
/// Covers the mapping ADR-006's own table specifies -- the resolver was
/// previously never calling anything like this at all, so every source
/// (including genuine Dolby Vision) silently went through the plain SDR
/// analysis graph regardless of its real range.
/// </summary>
public sealed class HdrVideoProfileMapperTests
{
    [Theory]
    [InlineData(VideoRangeType.HDR10)]
    [InlineData(VideoRangeType.HDR10Plus)]
    [InlineData(VideoRangeType.DOVIWithHDR10)]
    [InlineData(VideoRangeType.DOVIWithHDR10Plus)]
    [InlineData(VideoRangeType.DOVIWithEL)]
    [InlineData(VideoRangeType.DOVIWithELHDR10Plus)]
    public void RangesWithAnHdr10CompatibleBaseLayerUseTheHdr10Graph(VideoRangeType videoRangeType)
    {
        var profile = HdrVideoProfileMapper.Resolve(videoRangeType, NullLogger.Instance);

        Assert.Equal(HdrVideoProfile.Hdr10, profile);
    }

    [Theory]
    [InlineData(VideoRangeType.HLG)]
    [InlineData(VideoRangeType.DOVIWithHLG)]
    public void RangesWithAnHlgCompatibleBaseLayerUseTheHlgGraph(VideoRangeType videoRangeType)
    {
        var profile = HdrVideoProfileMapper.Resolve(videoRangeType, NullLogger.Instance);

        Assert.Equal(HdrVideoProfile.Hlg, profile);
    }

    [Fact]
    public void TheHardNoCrossCompatibilityCaseAppliesTheRpu()
    {
        var profile = HdrVideoProfileMapper.Resolve(VideoRangeType.DOVI, NullLogger.Instance);

        Assert.Equal(HdrVideoProfile.DolbyVision, profile);
    }

    [Fact]
    public void AnInvalidDolbyVisionStreamFallsBackToHdr10RatherThanTheRpu()
    {
        var profile = HdrVideoProfileMapper.Resolve(VideoRangeType.DOVIInvalid, NullLogger.Instance);

        Assert.Equal(HdrVideoProfile.Hdr10, profile);
    }

    [Theory]
    [InlineData(VideoRangeType.SDR)]
    [InlineData(VideoRangeType.DOVIWithSDR)]
    [InlineData(VideoRangeType.Unknown)]
    public void SdrAndSdrCompatibleRangesUseThePlainSdrGraph(VideoRangeType videoRangeType)
    {
        var profile = HdrVideoProfileMapper.Resolve(videoRangeType, NullLogger.Instance);

        Assert.Null(profile);
    }
}
