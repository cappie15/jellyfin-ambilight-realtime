using Jellyfin.Plugin.RealtimeAmbilight.Core.Output;
using Jellyfin.Plugin.RealtimeAmbilight.Core.Protocol;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Wled;

/// <summary>
/// WLED's data-plane lifecycle. The driver uses UDP only for RGB frames and an
/// explicit JSON state release after a fade; it never writes persistent WLED
/// configuration.
/// </summary>
public sealed class WledRealtimeOutput : ILedFrameOutput, IDisposable
{
    public const int DdpPort = 4048;
    public const int HyperionRawRgbPort = 19446;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly WledEndpoint _endpoint;
    private readonly WledRealtimeProtocol _requestedProtocol;
    private readonly IUdpDatagramSender _udpSender;
    private readonly int _bytesPerLed;
    private byte[]? _lastFrame;
    private byte _nextDdpSequence = 1;

    /// <param name="bytesPerLed">
    /// 3 for RGB24 (the default) or 4 for RGBW32. RGBW32 always forces DDP,
    /// overriding <paramref name="requestedProtocol"/> when it asks for
    /// Hyperion Raw RGB or Auto: that transport has no RGBW variant.
    /// </param>
    public WledRealtimeOutput(
        WledEndpoint endpoint,
        WledRealtimeProtocol requestedProtocol,
        IUdpDatagramSender udpSender,
        int bytesPerLed = 3)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _requestedProtocol = requestedProtocol;
        _udpSender = udpSender ?? throw new ArgumentNullException(nameof(udpSender));
        _bytesPerLed = bytesPerLed;
    }

    public WledProtocolSelection? CurrentProtocol { get; private set; }

    public async Task SendFrameAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken)
    {
        ValidateFrame(rgb24Frame.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendFrameCoreAsync(rgb24Frame, cancellationToken).ConfigureAwait(false);
            _lastFrame = rgb24Frame.ToArray();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Actively retains WLED realtime control while output is frozen.</summary>
    public async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastFrame is not null)
            {
                await SendFrameCoreAsync(_lastFrame, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task FadeToBlackAsync(TimeSpan duration, int framesPerSecond, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(framesPerSecond, 0);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastFrame is null)
            {
                return;
            }

            var source = _lastFrame;
            var stepCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * framesPerSecond));
            var interval = TimeSpan.FromTicks(duration.Ticks / stepCount);
            for (var step = 1; step <= stepCount; step++)
            {
                var remaining = (float)(stepCount - step) / stepCount;
                var faded = new byte[source.Length];
                for (var index = 0; index < faded.Length; index++)
                {
                    faded[index] = (byte)MathF.Round(source[index] * remaining);
                }

                await SendFrameCoreAsync(faded, cancellationToken).ConfigureAwait(false);
                _lastFrame = faded;
                if (step < stepCount)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Returns realtime control to WLED without modifying its configuration.
    /// </summary>
    /// <remarks>
    /// Per ADR-004 the release is performed by ceasing transmission: WLED's own
    /// realtime timeout restores its effect. Measured against the reference
    /// controller on firmware 16.0.1 (<c>if.live.timeout = 25</c>), ceasing
    /// transmission returns control after 2.26 s, whereas additionally posting
    /// <c>{"live": false}</c> to <c>/json/state</c> re-arms the realtime lock and
    /// delays the handover to 5.06 s. Dropping the retained frame under the send
    /// gate guarantees no keepalive can resend after a release.
    /// </remarks>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastFrame = null;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose() => _sendGate.Dispose();

    private async Task SendFrameCoreAsync(ReadOnlyMemory<byte> rgb24Frame, CancellationToken cancellationToken)
    {
        if (_bytesPerLed == 4)
        {
            // Hyperion Raw RGB carries only three channels per LED, so RGBW
            // always goes over DDP regardless of the requested/Auto protocol.
            CurrentProtocol = new WledProtocolSelection(
                WledRealtimeProtocol.Ddp,
                "RGBW forces DDP because Hyperion Raw RGB has no white channel.");
            var rgbwPackets = DdpPacketizer.Packetize(rgb24Frame.Span, _nextDdpSequence, bytesPerLed: 4);
            foreach (var packet in rgbwPackets)
            {
                await _udpSender.SendAsync(packet, _endpoint.Host, DdpPort, cancellationToken).ConfigureAwait(false);
                _nextDdpSequence = DdpPacketizer.NextSequence(_nextDdpSequence);
            }

            return;
        }

        var selection = WledProtocolSelector.Select(_requestedProtocol, rgb24Frame.Length / 3);
        CurrentProtocol = selection;
        if (selection.Protocol == WledRealtimeProtocol.HyperionRawRgb)
        {
            var datagram = HyperionRawRgbPacketizer.Packetize(rgb24Frame.Span);
            await _udpSender.SendAsync(datagram, _endpoint.Host, HyperionRawRgbPort, cancellationToken).ConfigureAwait(false);
            return;
        }

        var packets = DdpPacketizer.Packetize(rgb24Frame.Span, _nextDdpSequence);
        foreach (var packet in packets)
        {
            await _udpSender.SendAsync(packet, _endpoint.Host, DdpPort, cancellationToken).ConfigureAwait(false);
            _nextDdpSequence = DdpPacketizer.NextSequence(_nextDdpSequence);
        }
    }

    private void ValidateFrame(ReadOnlySpan<byte> rgb24Frame)
    {
        if (rgb24Frame.IsEmpty || rgb24Frame.Length % _bytesPerLed != 0)
        {
            throw new ArgumentException(
                $"A WLED frame must contain at least one complete {(_bytesPerLed == 4 ? "RGBW quadruplet" : "RGB triplet")}.",
                nameof(rgb24Frame));
        }
    }
}
