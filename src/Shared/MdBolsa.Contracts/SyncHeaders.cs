namespace MdBolsa.Contracts;

// Header names are part of the contract, not an implementation detail of either
// side: the client sets them, the server reads them, and a typo in one is a
// silent auth failure. They're constants so both sides compile against the same
// spelling.
public static class SyncHeaders
{
    // The shared secret for this server. Phase 9's deliberately simple
    // authentication: one token per deployment, presented by every device.
    // See docs/decisions/0011-client-sync.md for what it does and doesn't buy.
    public const string Token = "X-MdBolsa-Token";

    // Which device is acting. Required on writes so the server can record who
    // wrote last (needed for conflict detection in Phase 10), and on deletes so
    // a tombstone says whose deletion it was.
    public const string Device = "X-MdBolsa-Device";
}
