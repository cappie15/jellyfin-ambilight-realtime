using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Sampling;

public class SceneAverageSamplerTests
{
    [Fact]
    public void AveragesAUniformFrameToThatColour()
    {
        var frame = CreateFrame(32, 32, static (_, _) => (200, 100, 50)); // B,G,R

        var average = SceneAverageSampler.SampleBgra(frame, 32, 32, default);

        // Uniform frame: every sampled pixel is identical, so the average
        // must reproduce the same per-channel ordering as the source bytes.
        Assert.True(average.Blue > average.Green);
        Assert.True(average.Green > average.Red);
        Assert.True(average.Red > 0f);
    }

    [Fact]
    public void AHalfWhiteHalfBlackFrameAveragesToRoughlyMidGrey()
    {
        var frame = CreateFrame(32, 32, static (x, _) => x < 16 ? (235, 235, 235) : (16, 16, 16));

        var average = SceneAverageSampler.SampleBgra(frame, 32, 32, default);

        // Not exactly 0.5: linear-light averaging of a half-and-half sRGB-ish
        // split is not the same as averaging the encoded bytes. Just checks
        // it lands meaningfully between the two extremes, not at either one.
        Assert.True(average.Red is > 0.05f and < 0.95f);
    }

    [Fact]
    public void CropExcludesLetterboxBarsFromTheAverage()
    {
        // Bright picture in the middle rows, black bars top and bottom.
        var frame = CreateFrame(32, 32, static (_, y) => y is >= 10 and < 22 ? (235, 235, 235) : (16, 16, 16));

        var withoutCrop = SceneAverageSampler.SampleBgra(frame, 32, 32, default);
        var withCrop = SceneAverageSampler.SampleBgra(frame, 32, 32, new CropInsets(Top: 10, Right: 0, Bottom: 10, Left: 0));

        // Cropping out the bars raises the average toward the bright picture.
        Assert.True(withCrop.Red > withoutCrop.Red);
    }

    private static byte[] CreateFrame(int width, int height, Func<int, int, (int B, int G, int R)> colourAt)
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

        return frame;
    }
}
