namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Output;

/// <summary>
/// Protocol-agnostic sink for a complete physical RGB24 frame. WLED lifecycle
/// operations deliberately live on its future concrete driver, not in sampling.
/// </summary>
public interface ILedFrameOutput
{
    Task SendFrameAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken);
}
