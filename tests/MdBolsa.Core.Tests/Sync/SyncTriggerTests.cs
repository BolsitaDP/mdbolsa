using MdBolsa.Core.Sync;

namespace MdBolsa.Core.Tests.Sync;

// The rules that decide whether a background sync runs. The failure rule is the
// one worth pinning down: a sync that retries a dead server forever is worse than
// no automatic sync at all, because it fills the status line with noise.
public class SyncTriggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Fact]
    public void TheFirstAutomaticSyncRuns() =>
        Assert.Equal(SyncDecision.Run, SyncTrigger.Decide(SyncTriggerState.Never, Now, Interval));

    [Fact]
    public void ASyncThatJustRanDoesNotRunAgain() =>
        Assert.Equal(
            SyncDecision.NotYet,
            SyncTrigger.Decide(new SyncTriggerState(Now.AddMinutes(-1), 0), Now, Interval));

    [Fact]
    public void ASyncRunsOnceTheIntervalHasElapsed() =>
        Assert.Equal(
            SyncDecision.Run,
            SyncTrigger.Decide(new SyncTriggerState(Now.AddMinutes(-5), 0), Now, Interval));

    [Fact]
    public void AutomaticSyncIsPausedAfterOneFailure()
    {
        // One flaky moment should not leave the app quietly syncing in the
        // background forever - but it should also not take the manual button away.
        var state = SyncTrigger.Failed(new SyncTriggerState(Now.AddMinutes(-30), 0), Now);

        Assert.Equal(SyncDecision.Paused, SyncTrigger.Decide(state, Now, Interval));
    }

    [Fact]
    public void PausingLasts_UntilTheIntervalWouldHaveElapsedManyTimesOver() =>
        Assert.Equal(
            SyncDecision.Paused,
            SyncTrigger.Decide(new SyncTriggerState(Now.AddHours(-6), 3), Now, Interval));

    [Fact]
    public void ASuccessClearsTheFailureCount()
    {
        // Six minutes ago, so the interval has passed: this is the state after a
        // person pressed Sync and it worked, and automatic sync must be running
        // again. Asserting at the instant of the success would only prove the
        // timer reset, not that the pause was lifted.
        var recovered = SyncTrigger.Succeeded(new SyncTriggerState(Now.AddHours(-1), 4), Now.AddMinutes(-6));

        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(SyncDecision.Run, SyncTrigger.Decide(recovered, Now, Interval));
    }

    [Fact]
    public void SuccessAndFailureBothRecordWhenTheyHappened()
    {
        // Otherwise a failure would never age out and the pause would be permanent
        // even if the interval were short.
        var succeeded = SyncTrigger.Succeeded(SyncTriggerState.Never, Now);
        var failed = SyncTrigger.Failed(SyncTriggerState.Never, Now);

        Assert.Equal(Now, succeeded.LastAttempt);
        Assert.Equal(Now, failed.LastAttempt);
        Assert.Equal(0, succeeded.ConsecutiveFailures);
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    [Fact]
    public void RepeatedFailuresAreCounted() =>
        Assert.Equal(
            3,
            SyncTrigger
                .Failed(SyncTrigger.Failed(SyncTrigger.Failed(SyncTriggerState.Never, Now), Now), Now)
                .ConsecutiveFailures);
}
