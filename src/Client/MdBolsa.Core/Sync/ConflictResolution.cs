namespace MdBolsa.Core.Sync;

/// <summary>What a person chose to do about a conflicted note.</summary>
public enum ConflictResolution
{
    /// <summary>Nothing chosen yet. The note stays out of sync in both directions.</summary>
    Unresolved,

    /// <summary>Keep what this device has, and push it over the server's version.</summary>
    KeepLocal,

    /// <summary>Take what the server has, overwriting this device's copy.</summary>
    TakeRemote,

    /// <summary>Put an earlier revision back, as the note's current content.</summary>
    Restore,
}

public sealed record ConflictResolutionResult(bool Resolved, ConflictResolution Choice, string? Error = null)
{
    public static ConflictResolutionResult Failed(string error) =>
        new(false, ConflictResolution.Unresolved, error);
}
