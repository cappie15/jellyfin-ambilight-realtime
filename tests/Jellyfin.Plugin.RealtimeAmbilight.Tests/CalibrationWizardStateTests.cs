using Jellyfin.Plugin.RealtimeAmbilight;
using Xunit;

namespace Jellyfin.Plugin.RealtimeAmbilight.Tests;

public class CalibrationWizardStateTests
{
    [Fact]
    public void StaysArmedWhileWellWithinTheIdleTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        var wizard = new CalibrationWizardState(() => now);
        wizard.Arm();

        now += CalibrationWizardState.IdleTimeout - TimeSpan.FromSeconds(1);

        Assert.True(wizard.IsArmed);
    }

    [Fact]
    public void DisarmsItselfAfterTheIdleTimeoutWithNoActivityAtAll()
    {
        // Regression test for a real gap: an admin who arms the wizard then
        // closes the settings tab (or the browser crashes) before ever
        // reaching Finish/Cancel used to leave the anonymous TV surface --
        // including the unauthenticated photo upload -- reachable forever.
        var now = DateTimeOffset.UtcNow;
        var wizard = new CalibrationWizardState(() => now);
        wizard.Arm();

        now += CalibrationWizardState.IdleTimeout + TimeSpan.FromSeconds(1);

        Assert.False(wizard.IsArmed);
    }

    [Fact]
    public void ARunningTvPollResetsTheIdleTimeoutSoALongCalibrationNeverExpiresMidUse()
    {
        var now = DateTimeOffset.UtcNow;
        var wizard = new CalibrationWizardState(() => now);
        wizard.Arm();

        // Several idle-timeout-length stretches, each kept alive by a poll
        // just before it would have expired.
        for (var i = 0; i < 3; i++)
        {
            now += CalibrationWizardState.IdleTimeout - TimeSpan.FromSeconds(1);
            wizard.NoteTvPoll();
        }

        Assert.True(wizard.IsArmed);
    }

    [Fact]
    public void MovingAStepFromTheSettingsPageAlsoResetsTheIdleTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        var wizard = new CalibrationWizardState(() => now);
        wizard.Arm();

        now += CalibrationWizardState.IdleTimeout - TimeSpan.FromSeconds(1);
        wizard.MoveTo(1);
        now += CalibrationWizardState.IdleTimeout - TimeSpan.FromSeconds(1);

        Assert.True(wizard.IsArmed);
    }

    [Fact]
    public void ReArmingAfterAnIdleTimeoutStartsACleanSession()
    {
        var now = DateTimeOffset.UtcNow;
        var wizard = new CalibrationWizardState(() => now);
        wizard.Arm();
        wizard.MoveTo(3);

        now += CalibrationWizardState.IdleTimeout + TimeSpan.FromSeconds(1);
        Assert.False(wizard.IsArmed);

        wizard.Arm();

        Assert.True(wizard.IsArmed);
        Assert.Equal(0, wizard.StepIndex);
    }
}
