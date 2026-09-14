using Jellyfin.Plugin.RealtimeAmbilight.Core.Color;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Decoding;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Model;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue.Protocol;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// Turns analysis frames into DTLS packets for <see cref="HueEntertainmentService"/> --
/// split out because it is a self-contained concern (schedule a frame, build
/// its packet, remember what was last sent) that does not need to know
/// anything about the connection lifecycle, the state machine, or the pump
/// loop's own timing around it.
/// </summary>
public sealed class HueFrameTransmitter
{
    /// <summary>
    /// Mirrors <c>JellyfinWledOutputService.RunPumpAsync</c>'s own
    /// pendingFrames queue: the shared analysis decoder runs ~5 s ahead of
    /// the actual playback position (<c>FfmpegAnalysisWorker.DecoderLead</c>),
    /// so the newest published <see cref="AnalysisFrame"/> is not what the
    /// picture looks like right now -- it is what it will look like in a
    /// few seconds. Sending it immediately, as this queue exists to prevent,
    /// is what made Hue lights lag noticeably behind the picture regardless
    /// of how reactive <c>HueResponsePercent</c> was set: that control only
    /// smooths a frame that already arrived on time, it cannot correct one
    /// that arrived at the wrong time to begin with.
    /// </summary>
    private readonly Queue<(DateTimeOffset DueAt, AnalysisFrame Frame)> _pendingFrames = new();

    /// <summary>The last packet actually sent -- resent as a keepalive while paused.</summary>
    private byte[]? _lastPacket;

    /// <summary>Per-channel colours behind <see cref="_lastPacket"/>, for the caller's own fade-to-black to ease down from at stop.</summary>
    private IReadOnlyDictionary<int, LinearRgb>? _lastChannelColours;

    /// <summary>When something was last actually sent, so keepalives are not resent needlessly often.</summary>
    private DateTimeOffset _lastSendAt = DateTimeOffset.MinValue;

    /// <summary>Per-channel colours behind the last packet actually sent, or <see langword="null"/> if nothing has been sent since the last <see cref="Reset"/>.</summary>
    public IReadOnlyDictionary<int, LinearRgb>? LastChannelColours => _lastChannelColours;

    /// <summary>Clears everything remembered -- e.g. on a fresh connect or after fully stopping.</summary>
    public void Reset()
    {
        _pendingFrames.Clear();
        _lastPacket = null;
        _lastChannelColours = null;
    }

    /// <summary>Queues a frame to be rendered once <paramref name="dueAt"/> arrives, scheduling it against playback position rather than the moment it was decoded.</summary>
    public void EnqueueFrame(AnalysisFrame frame, DateTimeOffset dueAt) => _pendingFrames.Enqueue((dueAt, frame));

    /// <summary>
    /// The next packet to send, or <see langword="null"/> if nothing should
    /// be sent right now: the newest frame that has become due is rendered
    /// and packetized; otherwise, while paused or waiting on a frame, the
    /// last packet is resent as a keepalive once <paramref name="keepAliveInterval"/>
    /// has elapsed since the last send (or not at all, if nothing has ever
    /// been sent).
    /// </summary>
    public byte[]? BuildNextPacket(
        HueFrameProcessor frameProcessor,
        IReadOnlyList<HueEntertainmentChannel> channels,
        double brightnessFraction,
        Guid entertainmentConfigurationId,
        TimeSpan keepAliveInterval,
        DateTimeOffset now)
    {
        AnalysisFrame? dueFrame = null;
        while (_pendingFrames.TryPeek(out var pending) && pending.DueAt <= now)
        {
            // Keep only the newest frame that has actually become due --
            // an older one still queued behind it is already stale.
            dueFrame = _pendingFrames.Dequeue().Frame;
        }

        if (dueFrame is not null)
        {
            var colours = frameProcessor.Process(dueFrame, channels, brightnessFraction);
            var channelColours = channels
                .Select(c => new HueStreamPacketizer.ChannelColour(
                    (byte)c.ChannelId,
                    colours.TryGetValue(c.ChannelId, out var colour) ? colour : default(LinearRgb)))
                .ToArray();
            var packet = HueStreamPacketizer.Packetize(entertainmentConfigurationId, channelColours)[0];
            _lastPacket = packet;
            _lastChannelColours = colours;
            return packet;
        }

        if (now - _lastSendAt < keepAliveInterval)
        {
            return null; // Paused, or no new frame yet: hold, do not resend every 40 ms.
        }

        return _lastPacket; // Paused: keep the entertainment session valid with the last colours (or null, if nothing has ever been sent).
    }

    /// <summary>Records that <paramref name="now"/> is when a packet was last actually put on the wire, for <see cref="BuildNextPacket"/>'s own keepalive pacing.</summary>
    public void MarkSent(DateTimeOffset now) => _lastSendAt = now;

    /// <summary>
    /// One fade-out step's worth of channel colours: each of <paramref name="source"/>'s
    /// remembered colours scaled toward black by <paramref name="remaining"/>
    /// (1 = unscaled, 0 = black), keyed to every one of <paramref name="channels"/>
    /// so a channel with no remembered colour still gets an explicit black
    /// entry rather than being silently omitted from the packet. Pure and
    /// static specifically so the actual ramp values are directly testable
    /// without needing a real DTLS channel or the pump's own timing.
    /// </summary>
    public static HueStreamPacketizer.ChannelColour[] ComputeFadeStepColours(
        IReadOnlyList<HueEntertainmentChannel> channels,
        IReadOnlyDictionary<int, LinearRgb> source,
        float remaining)
        => channels
            .Select(c => new HueStreamPacketizer.ChannelColour(
                (byte)c.ChannelId,
                source.TryGetValue(c.ChannelId, out var colour)
                    ? new LinearRgb(colour.Red * remaining, colour.Green * remaining, colour.Blue * remaining)
                    : default))
            .ToArray();
}
