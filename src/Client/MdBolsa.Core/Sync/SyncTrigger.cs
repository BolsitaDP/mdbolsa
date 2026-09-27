namespace MdBolsa.Core.Sync;

// When an automatic sync should run, as a pure decision.
//
// The shell owns the clock; this owns the judgement, so the rules can be tested
// without waiting for real time to pass. That split matters more than usual here:
// the failure rule below is the one that keeps a background sync from retrying a
// dead server every thirty seconds for the rest of the session.
//
// Two triggers exist, and they are deliberately different:
//
//   * **Periodic** - every `interval`, so changes made on another device arrive
//     without anyone pressing anything. Runs whether or not this device changed
//     anything, because the point is the *other* direction.
//   * **Debounce** - a fixed quiet period after this device writes something, so a
//     burst of saves is one sync instead of ten. No decision logic needed: the
//     shell restarts a one-shot timer on each write and that is the whole rule.
//
// Neither ever runs on the save path. Typing must not wait for the network, and a
// note is already on disk before any of this looks at it.
public enum SyncDecision
{
    /// <summary>Nothing to do yet - the interval hasn't elapsed.</summary>
    NotYet,

    /// <summary>Go ahead now.</summary>
    Run,

    /// <summary>A previous automatic sync failed, so automatic sync is off. Only a
    /// sync the user asked for can turn it back on.</summary>
    Paused,
}

/// <param name="LastAttempt">When a sync last started, automatic or manual.</param>
/// <param name="ConsecutiveFailures">Automatic failures since the last success.</param>
public sealed record SyncTriggerState(DateTimeOffset? LastAttempt, int ConsecutiveFailures)
{
    public static readonly SyncTriggerState Never = new(null, 0);
}

public static class SyncTrigger
{
    public static SyncDecision Decide(
        SyncTriggerState state, DateTimeOffset now, TimeSpan interval)
    {
        // A failure pauses automatic sync rather than backing off and retrying. A
        // server that isn't there yet - the Pi is off, the network is down, the
        // token was rotated - will still not be there in four minutes, and a
        // background task that fails every thirty seconds fills the status line
        // with noise nobody asked for. The manual button stays, because a person
        // who has just started the server *wants* the retry.
        if (state.ConsecutiveFailures > 0) return SyncDecision.Paused;

        if (state.LastAttempt is null) return SyncDecision.Run;

        return now - state.LastAttempt >= interval ? SyncDecision.Run : SyncDecision.NotYet;
    }

    // What a successful sync does to the state, and what a failure does. Separate
    // from Decide so the caller cannot forget one of them - forgetting to reset
    // ConsecutiveFailures is exactly the bug that would leave sync paused forever
    // after one flaky moment.
    public static SyncTriggerState Succeeded(SyncTriggerState state, DateTimeOffset now) =>
        state with { LastAttempt = now, ConsecutiveFailures = 0 };

    public static SyncTriggerState Failed(SyncTriggerState state, DateTimeOffset now) =>
        state with { LastAttempt = now, ConsecutiveFailures = state.ConsecutiveFailures + 1 };
}
