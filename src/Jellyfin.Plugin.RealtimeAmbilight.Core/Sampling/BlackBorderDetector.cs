using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>
/// Finds the letterbox and pillarbox bars around the active picture so that
/// sampling follows the image instead of the bars.
/// </summary>
/// <remarks>
/// Two failure modes shape this design. A detector that reacts to every frame
/// makes the LEDs twitch whenever a dark shot briefly resembles a bar, so a new
/// result must repeat before it is adopted. And during a fade to black the whole
/// frame is "bar", which would crop the picture out of existence: a result that
/// leaves too little picture is rejected outright and the previous one is kept.
/// </remarks>
public sealed class BlackBorderDetector
{
    /// <summary>Rec.709 luma at or below which a pixel counts as black.</summary>
    public const int DefaultLumaThreshold = 16;

    /// <summary>Largest share of one axis that may be treated as bar.</summary>
    private const double MaximumBarFraction = 0.30;

    private readonly int _lumaThreshold;
    private readonly int _stabilityFrames;
    private CropInsets _accepted;
    private CropInsets _candidate;
    private int _candidateStreak;

    public BlackBorderDetector(int lumaThreshold = DefaultLumaThreshold, int stabilityFrames = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lumaThreshold);
        ArgumentOutOfRangeException.ThrowIfLessThan(stabilityFrames, 1);
        _lumaThreshold = lumaThreshold;
        _stabilityFrames = stabilityFrames;
    }

    /// <summary>The insets currently in effect.</summary>
    public CropInsets Current => _accepted;

    /// <summary>Measures <paramref name="frame"/> and returns the insets to sample with.</summary>
    public CropInsets Detect(AnalysisFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var measured = Measure(frame);
        if (measured is null)
        {
            // A frame that is dark all over says nothing about where the bars are.
            _candidateStreak = 0;
            return _accepted;
        }

        if (measured.Value == _candidate)
        {
            _candidateStreak++;
        }
        else
        {
            _candidate = measured.Value;
            _candidateStreak = 1;
        }

        if (_candidateStreak >= _stabilityFrames)
        {
            _accepted = _candidate;
        }

        return _accepted;
    }

    private CropInsets? Measure(AnalysisFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var pixels = frame.BgraPixels;
        var maximumHorizontalBar = (int)(height * MaximumBarFraction);
        var maximumVerticalBar = (int)(width * MaximumBarFraction);

        var top = 0;
        while (top <= maximumHorizontalBar && IsRowBlack(pixels, width, top))
        {
            top++;
        }

        var bottom = 0;
        while (bottom <= maximumHorizontalBar && IsRowBlack(pixels, width, height - 1 - bottom))
        {
            bottom++;
        }

        var left = 0;
        while (left <= maximumVerticalBar && IsColumnBlack(pixels, width, height, left))
        {
            left++;
        }

        var right = 0;
        while (right <= maximumVerticalBar && IsColumnBlack(pixels, width, height, width - 1 - right))
        {
            right++;
        }

        // Anything this large is a dark scene, not a bar. Reporting it would crop
        // the picture away and freeze the LEDs on whatever survived.
        if (top > maximumHorizontalBar || bottom > maximumHorizontalBar
            || left > maximumVerticalBar || right > maximumVerticalBar)
        {
            return null;
        }

        return new CropInsets(top, right, bottom, left);
    }

    private bool IsRowBlack(byte[] pixels, int width, int row)
    {
        var offset = row * width * AnalysisFrameOptions.BytesPerPixel;
        for (var x = 0; x < width; x++)
        {
            if (!IsBlack(pixels, offset + (x * AnalysisFrameOptions.BytesPerPixel)))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsColumnBlack(byte[] pixels, int width, int height, int column)
    {
        for (var y = 0; y < height; y++)
        {
            if (!IsBlack(pixels, ((y * width) + column) * AnalysisFrameOptions.BytesPerPixel))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rec.709 luma of one BGRA pixel, compared against the threshold.</summary>
    private bool IsBlack(byte[] pixels, int offset)
    {
        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var red = pixels[offset + 2];
        var luma = ((red * 2126) + (green * 7152) + (blue * 722)) / 10000;
        return luma <= _lumaThreshold;
    }
}
