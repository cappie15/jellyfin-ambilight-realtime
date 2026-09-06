namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

public enum WledRealtimeProtocol
{
    Auto,
    HyperionRawRgb,
    Ddp,
}

public sealed record WledProtocolSelection(WledRealtimeProtocol Protocol, string Reason);

public static class WledProtocolSelector
{
    public static WledProtocolSelection Select(WledRealtimeProtocol requested, int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ledCount, 1);

        return requested switch
        {
            WledRealtimeProtocol.Auto when ledCount <= Protocol.HyperionRawRgbPacketizer.MaximumLedCount
                => new WledProtocolSelection(WledRealtimeProtocol.HyperionRawRgb, "Auto selected the provisional one-datagram Raw RGB baseline for this layout."),
            WledRealtimeProtocol.Auto
                => new WledProtocolSelection(WledRealtimeProtocol.Ddp, "Auto selected DDP because the layout exceeds Raw RGB's one-datagram limit."),
            WledRealtimeProtocol.HyperionRawRgb when ledCount > Protocol.HyperionRawRgbPacketizer.MaximumLedCount
                => throw new ArgumentOutOfRangeException(nameof(ledCount), $"Hyperion Raw RGB supports at most {Protocol.HyperionRawRgbPacketizer.MaximumLedCount} LEDs. Use DDP for this layout."),
            WledRealtimeProtocol.HyperionRawRgb
                => new WledProtocolSelection(WledRealtimeProtocol.HyperionRawRgb, "Selected by the expert override."),
            WledRealtimeProtocol.Ddp
                => new WledProtocolSelection(WledRealtimeProtocol.Ddp, "Selected by the expert override."),
            _ => throw new ArgumentOutOfRangeException(nameof(requested)),
        };
    }
}
