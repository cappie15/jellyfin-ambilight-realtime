namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;

public enum HueEntertainmentState
{
    /// <summary>No bridge paired, or pairing was undone. The default state.</summary>
    Unpaired,

    /// <summary>Paired, an entertainment configuration is selected, nothing is playing.</summary>
    Ready,

    /// <summary>The bound TV started playing; opening the DTLS session.</summary>
    Connecting,

    /// <summary>Streaming live colours.</summary>
    Streaming,

    /// <summary>
    /// Playback is paused. The DTLS session stays open and the last colours
    /// stay shown; this is not a disconnect.
    /// </summary>
    Paused,

    /// <summary>
    /// The connection was lost (or never completed) for a reason judged
    /// transient. Bounded, backed-off reconnect attempts are in progress
    /// while the relevant playback is still active.
    /// </summary>
    Recovering,

    /// <summary>Ending the DTLS session in an orderly way before returning to Ready.</summary>
    Stopping,

    /// <summary>
    /// Something requires the operator to go through pairing again: revoked
    /// credentials, or an entertainment configuration that no longer exists
    /// on the bridge. Reconnect attempts would not help and are not made.
    /// </summary>
    RelinkRequired,
}

/// <summary>Why the machine is in <see cref="HueEntertainmentState.Recovering"/> or <see cref="HueEntertainmentState.RelinkRequired"/>, for the settings page.</summary>
public enum HueEntertainmentIssue
{
    None,
    TemporarilyUnreachable,
    CredentialsRevoked,
    EntertainmentConfigurationMissing,
    StreamOwnershipLost,
}

public sealed record HueEntertainmentStateChanged(HueEntertainmentState State, HueEntertainmentIssue Issue);

/// <summary>
/// The lifecycle state machine described in the Hue integration's own spec:
/// unpaired, ready, connecting, streaming, paused, recovering, stopping,
/// relink-required. Pure and synchronous -- it records what state the
/// integration is in and rejects a transition that does not make sense from
/// the current one; it does not itself touch the network, a timer, or a
/// background task. The hosted service driving it owns all of that and calls
/// the method matching whatever actually happened.
/// </summary>
public sealed class HueEntertainmentStateMachine
{
    private readonly object _sync = new();

    public HueEntertainmentState State { get; private set; } = HueEntertainmentState.Unpaired;

    public HueEntertainmentIssue Issue { get; private set; } = HueEntertainmentIssue.None;

    public event Action<HueEntertainmentStateChanged>? Changed;

    public void Paired()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Unpaired, HueEntertainmentState.RelinkRequired], HueEntertainmentState.Ready);
        }
    }

    public void Unpaired()
    {
        lock (_sync)
        {
            Transition(HueEntertainmentState.Unpaired);
        }
    }

    /// <summary>The bound TV started (or resumed from a stop) and streaming should begin.</summary>
    public void ConnectRequested()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Ready, HueEntertainmentState.Recovering], HueEntertainmentState.Connecting);
        }
    }

    public void Connected()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Connecting, HueEntertainmentState.Recovering], HueEntertainmentState.Streaming);
        }
    }

    /// <summary>Playback paused; the DTLS session and its colours are held, not torn down.</summary>
    public void PlaybackPaused()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Streaming], HueEntertainmentState.Paused);
        }
    }

    public void PlaybackResumed()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Paused], HueEntertainmentState.Streaming);
        }
    }

    /// <summary>
    /// The connection failed or was lost for a reason expected to clear on
    /// its own: a network blip, the bridge briefly unreachable, or another
    /// application currently owns the stream. Reconnection with backoff is
    /// the caller's responsibility; this only records why.
    /// </summary>
    public void ConnectionLost(HueEntertainmentIssue issue)
    {
        if (issue is not (HueEntertainmentIssue.TemporarilyUnreachable or HueEntertainmentIssue.StreamOwnershipLost))
        {
            throw new ArgumentOutOfRangeException(nameof(issue), issue, "Only a transient issue recovers by reconnecting.");
        }

        lock (_sync)
        {
            TransitionFrom(
                [HueEntertainmentState.Connecting, HueEntertainmentState.Streaming, HueEntertainmentState.Paused, HueEntertainmentState.Recovering],
                HueEntertainmentState.Recovering,
                issue);
        }
    }

    /// <summary>
    /// Something recovery cannot fix: the bridge revoked the application key
    /// (or client key), or the selected entertainment configuration no
    /// longer exists. Only going through pairing again resolves this.
    /// </summary>
    public void RequiresRelink(HueEntertainmentIssue issue)
    {
        if (issue is not (HueEntertainmentIssue.CredentialsRevoked or HueEntertainmentIssue.EntertainmentConfigurationMissing))
        {
            throw new ArgumentOutOfRangeException(nameof(issue), issue, "Only a permanent issue requires relinking.");
        }

        lock (_sync)
        {
            Transition(HueEntertainmentState.RelinkRequired, issue);
        }
    }

    /// <summary>Playback stopped, output is disabled, or the operator asked to stop.</summary>
    public void StopRequested()
    {
        lock (_sync)
        {
            TransitionFrom(
                [HueEntertainmentState.Connecting, HueEntertainmentState.Streaming, HueEntertainmentState.Paused, HueEntertainmentState.Recovering],
                HueEntertainmentState.Stopping);
        }
    }

    public void StopCompleted()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Stopping], HueEntertainmentState.Ready);
        }
    }

    /// <summary>
    /// Recovery gave up: the bound playback itself ended while
    /// <see cref="HueEntertainmentState.Recovering"/>, so there is nothing
    /// left to reconnect for. Returns to <see cref="HueEntertainmentState.Ready"/>
    /// directly, since no DTLS session is actually open to stop.
    /// </summary>
    public void RecoveryAbandoned()
    {
        lock (_sync)
        {
            TransitionFrom([HueEntertainmentState.Recovering], HueEntertainmentState.Ready);
        }
    }

    private void TransitionFrom(IReadOnlyCollection<HueEntertainmentState> validFrom, HueEntertainmentState to, HueEntertainmentIssue issue = HueEntertainmentIssue.None)
    {
        if (!validFrom.Contains(State))
        {
            throw new InvalidOperationException($"Cannot move to {to} from {State}; expected one of [{string.Join(", ", validFrom)}].");
        }

        Transition(to, issue);
    }

    private void Transition(HueEntertainmentState to, HueEntertainmentIssue issue = HueEntertainmentIssue.None)
    {
        State = to;
        Issue = issue;
        Changed?.Invoke(new HueEntertainmentStateChanged(to, issue));
    }
}
