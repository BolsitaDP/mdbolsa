namespace MdBolsa.Core.Sync;

/// <summary>Outcome of one sync run, for the UI to report without parsing anything.</summary>
public sealed record SyncResult(
    int Pushed,
    int Pulled,
    int Deleted,
    int Conflicts,
    bool Failed = false,
    string? Error = null)
{
    public static SyncResult Failure(string error) => new(0, 0, 0, 0, Failed: true, Error: error);
}
