using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Playback;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueFrameProcessorTests
{
    [Fact]
    public void EmptyChannelListProducesNoOutputWithoutSamplingAnything()
    {
        var processor = new HueFrameProcessor(new ManualMonotonicTime());
        var frame = CreateFrame(64, 64, static (_, _) => (16, 16, 16));

        var result = processor.Process(frame, []);

        Assert.Empty(result);
    }

    [Fact]
    public void AChannelNearTheTopOfTheScreenFollowsTheBrightTopHalfNotTheDarkBottomHalf()
    {
        var time = new ManualMonotonicTime();
        var processor = new HueFrameProcessor(time);
        var frame = CreateFrame(64, 64, static (_, y) => y < 32 ? (235, 235, 235) : (16, 16, 16));
        var channels = new[] { new HueEntertainmentChannel(1, new HuePosition(0, 0, 1), []) };

        // First call snaps (no previous smoothing state), so it would read
        // the frame's own colour directly rather than easing toward it --
        // but the session's own 2000 ms fade-in still holds the very first
        // frame near black regardless, so the clock is advanced past that
        // window first to observe the frame processor's steady-state colour
        // rather than its deliberate start-of-session fade.
        processor.Process(frame, channels);
        time.Advance(TimeSpan.FromMilliseconds(2100));
        var result = processor.Process(frame, channels);

        Assert.True(result[1].Red > 0.3f);
    }

    [Fact]
    public void AChannelBehindTheViewerFollowsTheWholeSceneNotJustOneEdge()
    {
        var time = new ManualMonotonicTime();
        var processor = new HueFrameProcessor(time);
        // Bright picture, but only at the very edges are the sampled bands
        // dark -- the scene average should still read as bright overall,
        // unlike a channel that only ever looked at the top edge.
        var frame = CreateFrame(64, 64, static (x, y) =>
            (x < 4 || x >= 60 || y < 4 || y >= 60) ? (0, 0, 0) : (235, 235, 235));
        var behindViewer = new[] { new HueEntertainmentChannel(1, new HuePosition(0, 1, 0.5), []) };

        // Past the session's own 2000 ms fade-in -- see the top-half test's
        // own comment for why.
        processor.Process(frame, behindViewer);
        time.Advance(TimeSpan.FromMilliseconds(2100));
        var result = processor.Process(frame, behindViewer);

        Assert.True(result[1].Red > 0.3f);
    }

    [Fact]
    public void ANewSessionFadesInFromBlackOverTheFirst2000Milliseconds()
    {
        var time = new ManualMonotonicTime();
        var processor = new HueFrameProcessor(time);
        var frame = CreateFrame(32, 32, static (_, _) => (235, 235, 235));
        var channels = new[] { new HueEntertainmentChannel(1, new HuePosition(0, 0, 0), []) };

        var firstFrame = processor.Process(frame, channels);
        Assert.Equal(0f, firstFrame[1].Red);

        time.Advance(TimeSpan.FromMilliseconds(1000));
        var halfway = processor.Process(frame, channels);

        time.Advance(TimeSpan.FromMilliseconds(1100));
        var pastFadeIn = processor.Process(frame, channels);

        Assert.True(halfway[1].Red > 0f, "expected some brightness partway through the fade-in");
        Assert.True(pastFadeIn[1].Red > halfway[1].Red, "expected full brightness once the fade-in has finished");
    }

    [Fact]
    public void ElapsedTimeIsMeasuredFromTheInjectedMonotonicClockNotTheWallClock()
    {
        var time = new ManualMonotonicTime();
        var processor = new HueFrameProcessor(time);
        var frame = CreateFrame(32, 32, static (_, _) => (16, 16, 16));
        var channels = new[] { new HueEntertainmentChannel(1, new HuePosition(0, 0, 0), []) };

        processor.Process(frame, channels); // first call: snaps, establishes _lastProcessedAt
        time.Advance(TimeSpan.FromMilliseconds(33));
        var brightFrame = CreateFrame(32, 32, static (_, _) => (235, 235, 235));

        // A rate-limited natural-light filter downstream means one 33 ms
        // frame from black cannot jump straight to full brightness -- this
        // only holds if elapsed time actually came from the advanced clock.
        var result = processor.Process(brightFrame, channels);

        Assert.True(result[1].Red < 0.9f);
    }

    private static AnalysisFrame CreateFrame(int width, int height, Func<int, int, (int B, int G, int R)> colourAt)
    {
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r) = colourAt(x, y);
                var offset = ((y * width) + x) * 4;
                frame[offset] = (byte)b;
                frame[offset + 1] = (byte)g;
                frame[offset + 2] = (byte)r;
                frame[offset + 3] = 255;
            }
        }

        return new AnalysisFrame(frame, width, height);
    }

    private sealed class ManualMonotonicTime : IMonotonicTime
    {
        public TimeSpan Elapsed { get; private set; }

        public void Advance(TimeSpan duration) => Elapsed += duration;
    }
}
