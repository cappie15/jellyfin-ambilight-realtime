using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Decoding;

public class FfmpegAnalysisCommandBuilderTests
{
    [Fact]
    public void SdrCommandUsesInputSideSeekAndASeparatedArgumentVector()
    {
        var source = CreateSource(mediaPath: "/media/film with spaces.mkv");

        var startInfo = FfmpegAnalysisCommandBuilder.CreateSdrStartInfo(source, TimeSpan.FromMilliseconds(1500));
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(arguments.IndexOf("-ss") < arguments.IndexOf("-i"));
        Assert.Equal("1.5", arguments[arguments.IndexOf("-ss") + 1]);
        Assert.Equal("/media/film with spaces.mkv", arguments[arguments.IndexOf("-i") + 1]);
        Assert.Contains("-re", arguments);
        Assert.Equal("pipe:1", arguments[^1]);
    }

    [Theory]
    [InlineData(HdrVideoProfile.Hdr10, "apply_dolbyvision=false")]
    [InlineData(HdrVideoProfile.Hlg, "apply_dolbyvision=false")]
    [InlineData(HdrVideoProfile.DolbyVision, "apply_dolbyvision=true")]
    public void HdrArgumentsPinHardwareGraphAndToneMapping(HdrVideoProfile profile, string dolbyOption)
    {
        var arguments = FfmpegHdrAnalysisCommandBuilder.BuildArguments(
            CreateSource(), TimeSpan.FromSeconds(2), profile).ToArray();

        Assert.Equal("-ss", arguments[4]);
        Assert.Equal("2", arguments[5]);
        Assert.Contains("-hwaccel", arguments);

        // libplacebo is a Vulkan filter: without its own filter device the graph
        // fails to negotiate and the analysis produces no frames at all.
        Assert.Contains("-init_hw_device", arguments);
        Assert.Contains("vulkan=vk", arguments);
        Assert.Equal("vk", arguments[arguments.IndexOf("-filter_hw_device") + 1]);
        var filter = arguments[Array.IndexOf(arguments, "-vf") + 1];
        Assert.Contains("scale_vaapi=w=960:h=540", filter);
        Assert.Contains("libplacebo", filter);
        Assert.Contains("colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv", filter);
        Assert.Contains("tonemapping=bt.2390", filter);
        Assert.Contains(dolbyOption, filter);
        Assert.Contains("format=bgra", filter);
    }

    [Fact]
    public void HdrBuilderRejectsRelativeDevice()
    {
        Assert.Throws<ArgumentException>(() => FfmpegHdrAnalysisCommandBuilder.BuildArguments(
            CreateSource(), TimeSpan.Zero, HdrVideoProfile.Hdr10, "renderD128"));
    }

    [Fact]
    public void SdrCommandDefinesFixedRealtimeBgraFrames()
    {
        var source = new FfmpegAnalysisSource(
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/media/film.mkv",
            new AnalysisFrameOptions { Width = 320, Height = 180, FramesPerSecond = 24 });

        var arguments = FfmpegAnalysisCommandBuilder.BuildSdrArguments(source, TimeSpan.Zero).ToArray();

        Assert.Equal(320 * 180 * AnalysisFrameOptions.BytesPerPixel, source.FrameOptions.ByteLength);
        Assert.Equal("fps=24,scale=320:180:flags=bilinear,format=bgra", arguments[arguments.IndexOf("-vf") + 1]);
        Assert.Equal("bgra", arguments[arguments.IndexOf("-pix_fmt") + 1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void FrameOptionsRejectAnUnsafeFrameRate(int framesPerSecond)
    {
        var options = new AnalysisFrameOptions { FramesPerSecond = framesPerSecond };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void CommandRejectsRelativePathsAndNegativeSeeks()
    {
        var relativeSource = CreateSource(mediaPath: "relative-film.mkv");

        Assert.Throws<ArgumentException>(() => FfmpegAnalysisCommandBuilder.BuildSdrArguments(relativeSource, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegAnalysisCommandBuilder.BuildSdrArguments(CreateSource(), TimeSpan.FromTicks(-1)));
    }

    private static FfmpegAnalysisSource CreateSource(string mediaPath = "/media/film.mkv")
        => new(
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            mediaPath,
            new AnalysisFrameOptions());
}
