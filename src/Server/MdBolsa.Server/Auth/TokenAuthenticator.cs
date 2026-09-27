namespace MdBolsa.Server.Auth;

/// <summary>
/// Whoever is making the request. Present on every authenticated request, so the
/// day somebody wants an audit log - "who deleted this note", "which device has
/// been uploading since March" - the answer is already being carried.
/// </summary>
public sealed record AuthenticatedCaller(string Name, bool IsSharedToken, Guid? DeviceId)
{
    /// <summary>The bootstrap credential itself, rather than a minted device.</summary>
    public static AuthenticatedCaller Shared { get; } = new("shared token", true, null);

    public static AuthenticatedCaller ForDevice(DeviceToken device) =>
        new(device.Name, false, device.Id);
}

/// <summary>
/// Decides whether a presented token is allowed in, and who it belongs to.
///
/// Two credentials, in this order:
///
/// 1. **The shared token** (`Sync:Token`). Constant-time comparison against a
///    config value, exactly as Phase 9 did. It keeps working, so this phase
///    breaks nothing, and it is the only credential allowed to mint or revoke a
///    device token.
/// 2. **A device token.** Hashed lookup, revoked tokens rejected.
///
/// The shared token is checked first on purpose. It is a comparison against a
/// value already in memory, and a device token is a database round trip - so
/// whichever credential the owner is more likely to be using by hand stays the
/// cheap path, and a wrong token of the wrong shape never reaches the database.
/// </summary>
public sealed class TokenAuthenticator(string? sharedToken, DeviceTokenStore store)
{
    /// <summary>
    /// Whether this credential may manage devices. Deliberately not "any valid
    /// token": a device token that could mint another device token would turn one
    /// stolen laptop into permanent access, which is the exact failure this phase
    /// exists to remove.
    /// </summary>
    public bool IsSharedToken(string? presented) =>
        TokenValidator.IsValid(presented, sharedToken);

    public async Task<AuthenticatedCaller?> VerifyAsync(
        string? presented, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;

        if (IsSharedToken(presented)) return AuthenticatedCaller.Shared;

        if (!DeviceTokenStore.LooksLikeDeviceToken(presented)) return null;

        var device = await store.FindByTokenAsync(presented, cancellationToken);
        return device is null ? null : AuthenticatedCaller.ForDevice(device);
    }
}
