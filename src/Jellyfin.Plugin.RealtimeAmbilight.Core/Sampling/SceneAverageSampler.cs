using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>
/// Averages every pixel of the active picture (inside the crop, excluding
/// letterbox/pillarbox bars) into one linear-light colour.
/// </summary>
/// <remarks>
/// This is deliberately not derived from <see cref="EdgeSampler"/>'s edge
/// zones: a Hue light placed behind the viewer should follow the scene as a
/// whole, and the mean of only the outer sampled band is a biased estimate of
/// that -- a scene with a bright centre and dark edges (a spotlit subject
/// against a dark background, common in film) would read as much darker than
/// it looks. To keep the cost bounded on a large analysis frame, every pixel
/// is sampled at a fixed stride rather than every single one; the analysis
/// frame is already small (tens of thousands of pixels), and a beauty average
/// like this does not need every pixel to be stable frame to frame.
/// </remarks>
public static class SceneAverageSampler
{
    /// <summary>
    /// Upper bound on how many pixels are actually averaged, by skipping
    /// pixels at a stride when the active picture holds more than this. Keeps
    /// the cost of a second, whole-picture pass bounded and roughly constant
    /// regardless of analysis resolution, rather than scaling with it.
    /// </summary>
    private const int MaximumSampledPixels = 4096;

    public static LinearRgb SampleBgra(ReadOnlySpan<byte> bgraFrame, int frameWidth, int frameHeight, CropInsets crop)
    {
        if (frameWidth < 16 || frameHeight < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Sampling frames must be at least 16 by 16 pixels.");
        }

        var expectedLength = checked(frameWidth * frameHeight * 4);
        if (bgraFrame.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} BGRA bytes but received {bgraFrame.Length}.", nameof(bgraFrame));
        }

        crop.Validate(frameWidth, frameHeight);
        var left = crop.Left;
        var top = crop.Top;
        var right = frameWidth - crop.Right;
        var bottom = frameHeight - crop.Bottom;
        var activeWidth = right - left;
        var activeHeight = bottom - top;
        if (activeWidth < 1 || activeHeight < 1)
        {
            throw new ArgumentException("The active picture is empty after cropping.", nameof(crop));
        }

        var activePixelCount = (long)activeWidth * activeHeight;
        var stride = Math.Max(1, (int)Math.Sqrt((double)activePixelCount / MaximumSampledPixels));

        double red = 0;
        double green = 0;
        double blue = 0;
        long sampled = 0;
        for (var y = top; y < bottom; y += stride)
        {
            for (var x = left; x < right; x += stride)
            {
                var offset = checked(((y * frameWidth) + x) * 4);
                blue += Bt709LimitedToLinear(bgraFrame[offset]);
                green += Bt709LimitedToLinear(bgraFrame[offset + 1]);
                red += Bt709LimitedToLinear(bgraFrame[offset + 2]);
                sampled++;
            }
        }

        return new LinearRgb((float)(red / sampled), (float)(green / sampled), (float)(blue / sampled));
    }

    // Duplicated from EdgeSampler rather than shared: a small, stable, pure
    // formula, and exposing it publicly from EdgeSampler for one extra caller
    // was judged not worth touching that file for.
    private static double Bt709LimitedToLinear(byte codeValue)
    {
        var nonlinear = (codeValue - 16d) / 219d;
        return nonlinear < 0.081d
            ? nonlinear / 4.5d
            : Math.Pow((nonlinear + 0.099d) / 1.099d, 1d / 0.45d);
    }
}
