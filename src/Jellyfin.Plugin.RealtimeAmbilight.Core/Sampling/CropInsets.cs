namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Sampling;

/// <summary>Independent, non-negative crop insets into an analysis frame.</summary>
public readonly record struct CropInsets(int Top, int Right, int Bottom, int Left)
{
    public void Validate(int frameWidth, int frameHeight)
    {
        if (Top < 0 || Right < 0 || Bottom < 0 || Left < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Crop insets cannot be negative.");
        }

        if (Left + Right >= frameWidth || Top + Bottom >= frameHeight)
        {
            throw new ArgumentException("Crop insets must leave a non-empty active picture.", nameof(frameWidth));
        }
    }
}
