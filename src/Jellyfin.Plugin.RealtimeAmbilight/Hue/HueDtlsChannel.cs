using HueApi.Entertainment;

namespace Jellyfin.Plugin.RealtimeAmbilight.Hue;

/// <summary>
/// Wraps <see cref="StreamingHueClient"/> for exactly two things it does not
/// offer itself: a bounded, abandonable connect, and a way to send one raw
/// HueStream datagram this plugin built with its own <c>HueStreamPacketizer</c>
/// rather than the library's own layered <c>StreamingGroup</c>/<c>EntertainmentLayer</c>
/// effects state.
/// </summary>
/// <remarks>
/// <see cref="StreamingHueClient.ConnectAsync"/> bounds the initial UDP
/// socket connect with a 5 s <see cref="CancellationToken"/>, but the actual
/// DTLS handshake underneath it -- <c>DtlsClientProtocol.Connect</c>, from
/// BouncyCastle -- is a synchronous, blocking call with no cancellation
/// support at all; its own internal retransmission timeouts can hold the
/// calling thread for up to a minute. This is exactly the "a timeout on
/// opening a UDP socket is not a full handshake timeout" risk called out
/// up front. There is no way to cancel that inner call from outside the
/// library, so the mitigation here is to abandon it instead: run it on its
/// own dedicated thread (not a pooled one, since it may occupy that thread
/// for a long time) and, if this class's own timeout elapses first, close
/// the underlying socket, which unblocks the handshake's blocked read and
/// lets it fail out on its own rather than leaving a thread stuck forever.
/// </remarks>
public sealed class HueDtlsChannel : StreamingHueClient
{
    public HueDtlsChannel(string ip, string applicationKey, string clientKey)
        : base(ip, applicationKey, clientKey)
    {
    }

    /// <returns>True once connected; false on timeout or cancellation, with the connection abandoned either way.</returns>
    public async Task<bool> TryConnectAsync(Guid entertainmentConfigurationId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connectTask = Task.Factory.StartNew(
            () => ConnectAsync(entertainmentConfigurationId),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);

        var completed = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
        if (!ReferenceEquals(completed, connectTask))
        {
            // Abandon: close the socket to unblock the still-running
            // handshake thread rather than leave it blocked indefinitely.
            try
            {
                Close();
            }
            catch (Exception)
            {
                // Best-effort abandonment; the connect task is left to fail
                // out on its own once the socket it was using is gone.
            }

            return false;
        }

        await connectTask.ConfigureAwait(false); // observes/propagates a real connect failure
        return true;
    }

    /// <summary>Sends one already-built HueStream datagram (see <c>HueStreamPacketizer</c>) as-is.</summary>
    public void SendPacket(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Send(packet, 0, packet.Length);
    }
}
