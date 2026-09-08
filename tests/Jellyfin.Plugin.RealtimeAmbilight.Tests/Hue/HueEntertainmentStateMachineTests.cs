using Jellyfin.Plugin.RealtimeAmbilight.Core.Hue;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests.Hue;

public class HueEntertainmentStateMachineTests
{
    [Fact]
    public void StartsUnpaired()
    {
        var machine = new HueEntertainmentStateMachine();

        Assert.Equal(HueEntertainmentState.Unpaired, machine.State);
    }

    [Fact]
    public void TheHappyPathGoesUnpairedReadyConnectingStreamingStoppingReady()
    {
        var machine = new HueEntertainmentStateMachine();

        machine.Paired();
        Assert.Equal(HueEntertainmentState.Ready, machine.State);

        machine.ConnectRequested();
        Assert.Equal(HueEntertainmentState.Connecting, machine.State);

        machine.Connected();
        Assert.Equal(HueEntertainmentState.Streaming, machine.State);

        machine.StopRequested();
        Assert.Equal(HueEntertainmentState.Stopping, machine.State);

        machine.StopCompleted();
        Assert.Equal(HueEntertainmentState.Ready, machine.State);
    }

    [Fact]
    public void PausingAndResumingNeverLeavesStreamingOrPausedWithoutReconnecting()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();

        machine.PlaybackPaused();
        Assert.Equal(HueEntertainmentState.Paused, machine.State);

        machine.PlaybackResumed();
        Assert.Equal(HueEntertainmentState.Streaming, machine.State);
    }

    [Fact]
    public void ATransientConnectionLossMovesToRecoveringWithTheGivenIssue()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();

        machine.ConnectionLost(HueEntertainmentIssue.TemporarilyUnreachable);

        Assert.Equal(HueEntertainmentState.Recovering, machine.State);
        Assert.Equal(HueEntertainmentIssue.TemporarilyUnreachable, machine.Issue);
    }

    [Fact]
    public void RecoveringCanReconnectBackToStreaming()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();
        machine.ConnectionLost(HueEntertainmentIssue.TemporarilyUnreachable);

        machine.ConnectRequested();
        machine.Connected();

        Assert.Equal(HueEntertainmentState.Streaming, machine.State);
        Assert.Equal(HueEntertainmentIssue.None, machine.Issue);
    }

    [Fact]
    public void RevokedCredentialsRequireRelinkAndDoNotAutoRecover()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();

        machine.RequiresRelink(HueEntertainmentIssue.CredentialsRevoked);

        Assert.Equal(HueEntertainmentState.RelinkRequired, machine.State);
        Assert.Equal(HueEntertainmentIssue.CredentialsRevoked, machine.Issue);
    }

    [Fact]
    public void AMissingEntertainmentConfigurationRequiresRelinkToo()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();

        machine.RequiresRelink(HueEntertainmentIssue.EntertainmentConfigurationMissing);

        Assert.Equal(HueEntertainmentState.RelinkRequired, machine.State);
    }

    [Fact]
    public void PairingAgainFromRelinkRequiredReturnsToReady()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.RequiresRelink(HueEntertainmentIssue.CredentialsRevoked);

        machine.Paired();

        Assert.Equal(HueEntertainmentState.Ready, machine.State);
    }

    [Fact]
    public void RecoveryAbandonedReturnsDirectlyToReadyWithoutAStopStep()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();
        machine.ConnectionLost(HueEntertainmentIssue.TemporarilyUnreachable);

        machine.RecoveryAbandoned();

        Assert.Equal(HueEntertainmentState.Ready, machine.State);
    }

    [Fact]
    public void ConnectingBeforeEverPairingThrows()
    {
        var machine = new HueEntertainmentStateMachine();

        Assert.Throws<InvalidOperationException>(() => machine.ConnectRequested());
    }

    [Fact]
    public void PausingWhileNotStreamingThrows()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();

        Assert.Throws<InvalidOperationException>(() => machine.PlaybackPaused());
    }

    [Fact]
    public void ConnectionLostRejectsAPermanentIssue()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();
        machine.ConnectRequested();
        machine.Connected();

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.ConnectionLost(HueEntertainmentIssue.CredentialsRevoked));
    }

    [Fact]
    public void RequiresRelinkRejectsATransientIssue()
    {
        var machine = new HueEntertainmentStateMachine();
        machine.Paired();

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.RequiresRelink(HueEntertainmentIssue.TemporarilyUnreachable));
    }

    [Fact]
    public void ChangedFiresOnEveryTransitionWithTheNewStateAndIssue()
    {
        var machine = new HueEntertainmentStateMachine();
        var events = new List<HueEntertainmentStateChanged>();
        machine.Changed += events.Add;

        machine.Paired();
        machine.ConnectRequested();
        machine.ConnectionLost(HueEntertainmentIssue.StreamOwnershipLost);

        Assert.Equal(
            [
                new HueEntertainmentStateChanged(HueEntertainmentState.Ready, HueEntertainmentIssue.None),
                new HueEntertainmentStateChanged(HueEntertainmentState.Connecting, HueEntertainmentIssue.None),
                new HueEntertainmentStateChanged(HueEntertainmentState.Recovering, HueEntertainmentIssue.StreamOwnershipLost),
            ],
            events);
    }
}
