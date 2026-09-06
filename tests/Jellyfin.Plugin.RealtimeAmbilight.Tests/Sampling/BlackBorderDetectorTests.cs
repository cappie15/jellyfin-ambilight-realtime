using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Sampling;

public sealed class BlackBorderDetectorTests
{
    private const int Width = 64;
    private const int Height = 36;

    [Fact]
    public void LetterboxIsAdoptedOnceItHasRepeated()
    {
        var detector = new BlackBorderDetector(stabilityFrames: 3);
        var frame = Frame(topBar: 6, bottomBar: 6);

        // A single frame must not move the sampling window; a dark shot would.
        Assert.Equal(new CropInsets(0, 0, 0, 0), detector.Detect(frame));
        Assert.Equal(new CropInsets(0, 0, 0, 0), detector.Detect(frame));
        Assert.Equal(new CropInsets(6, 0, 6, 0), detector.Detect(frame));
    }

    [Fact]
    public void AFadeToBlackKeepsTheLastKnownGeometry()
    {
        var detector = new BlackBorderDetector(stabilityFrames: 2);
        var letterboxed = Frame(topBar: 4, bottomBar: 4);
        detector.Detect(letterboxed);
        detector.Detect(letterboxed);
        Assert.Equal(new CropInsets(4, 0, 4, 0), detector.Current);

        // Every pixel is now "bar". Cropping on that would erase the picture.
        var black = new AnalysisFrame(new byte[Width * Height * 4], Width, Height);
        Assert.Equal(new CropInsets(4, 0, 4, 0), detector.Detect(black));
        Assert.Equal(new CropInsets(4, 0, 4, 0), detector.Detect(black));
    }

    [Fact]
    public void AFullFrameReportsNoBars()
    {
        var detector = new BlackBorderDetector(stabilityFrames: 1);

        Assert.Equal(new CropInsets(0, 0, 0, 0), detector.Detect(Frame(topBar: 0, bottomBar: 0)));
    }

    [Fact]
    public void PillarboxIsDetectedOnTheHorizontalAxis()
    {
        var detector = new BlackBorderDetector(stabilityFrames: 1);

        Assert.Equal(new CropInsets(0, 5, 0, 5), detector.Detect(Frame(topBar: 0, bottomBar: 0, leftBar: 5, rightBar: 5)));
    }

    [Fact]
    public void ADarkButNotBlackBarIsNotCropped()
    {
        var detector = new BlackBorderDetector(lumaThreshold: 4, stabilityFrames: 1);

        // Luma 16 sits above a threshold of 4, so this is picture, not bar.
        Assert.Equal(new CropInsets(0, 0, 0, 0), detector.Detect(Frame(topBar: 6, bottomBar: 6, barLevel: 16)));
    }

    private static AnalysisFrame Frame(int topBar, int bottomBar, int leftBar = 0, int rightBar = 0, byte barLevel = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var isBar = y < topBar || y >= Height - bottomBar || x < leftBar || x >= Width - rightBar;
                var value = isBar ? barLevel : (byte)200;
                var offset = ((y * Width) + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return new AnalysisFrame(pixels, Width, Height);
    }
}
