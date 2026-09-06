using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Layout;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Output;

public class AmbilightFrameProcessorTests
{
    [Fact]
    public void ProcessConnectsBgraSamplingInterpolationAndRgb24Encoding()
    {
        var processor = CreateProcessor();

        var rgb24 = processor.Process(CreateSolidFrame(red: 235, green: 16, blue: 16));

        Assert.Equal(24, rgb24.Length);
        for (var offset = 0; offset < rgb24.Length; offset += 3)
        {
            Assert.Equal((byte)255, rgb24[offset]);
            Assert.Equal((byte)0, rgb24[offset + 1]);
            Assert.Equal((byte)0, rgb24[offset + 2]);
        }
    }

    [Fact]
    public async Task SchedulerDropsSupersededFramesAndSendsOnlyTheLatest()
    {
        var latestFrames = new LatestFrameBuffer<AnalysisFrame>();
        var output = new CapturingOutput();
        var scheduler = new LatestFrameOutputScheduler(latestFrames, CreateProcessor(), output);
        latestFrames.Publish(CreateSolidFrame(red: 235, green: 16, blue: 16));
        latestFrames.Publish(CreateSolidFrame(red: 16, green: 16, blue: 235));

        Assert.True(await scheduler.SendLatestAsync(CancellationToken.None));
        Assert.False(await scheduler.SendLatestAsync(CancellationToken.None));

        var frame = Assert.Single(output.Frames);
        for (var offset = 0; offset < frame.Length; offset += 3)
        {
            Assert.Equal((byte)0, frame[offset]);
            Assert.Equal((byte)0, frame[offset + 1]);
            Assert.Equal((byte)255, frame[offset + 2]);
        }
    }

    private static AmbilightFrameProcessor CreateProcessor()
        => new(
            new LedLayout(2, 2, 2, 2),
            new LogicalSamplingLayout(2, 2, 2, 2),
            static _ => new CropInsets());

    private static AnalysisFrame CreateSolidFrame(byte red, byte green, byte blue)
    {
        const int width = 20;
        const int height = 20;
        var pixels = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = byte.MaxValue;
        }

        return new AnalysisFrame(pixels, width, height);
    }

    private sealed class CapturingOutput : ILedFrameOutput
    {
        public List<byte[]> Frames { get; } = [];

        public Task SendFrameAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken)
        {
            Frames.Add(rgb24Frame.ToArray());
            return Task.CompletedTask;
        }
    }
}
