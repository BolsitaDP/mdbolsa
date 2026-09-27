using MdBolsa.Contracts;
using MdBolsa.Server.Auth;

namespace MdBolsa.Server.Endpoints;

/// <summary>What the client sends to ask for a device token.</summary>
public sealed record MintDeviceRequest(string? Name);

/// <summary>
/// The response to minting. It carries the token, and this is the only response
/// that ever will: the server kept a hash, so there is no second chance to ask.
/// </summary>
public sealed record MintedDevice(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);

/// <summary>A device in the list. No hash, no token - the listing is for a human.</summary>
public sealed record DeviceSummary(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool Revoked);

public static class DeviceTokenEndpoints
{
    /// <summary>Long enough to say "the laptop in my bag", short enough to read aloud.</summary>
    private const int MaxNameLength = 64;

    public static IEndpointRouteBuilder MapDeviceTokenEndpoints(
        this IEndpointRouteBuilder app, TokenAuthenticator auth)
    {
        var devices = app.MapGroup("/api/devices").WithTags("devices");

        // Every route here needs the **shared** token, not any valid token. Minting
        // and revoking are owner actions: they decide who can reach the vault at
        // all, so a device may not do them for itself or for anything else.
        devices.AddEndpointFilter(async (context, next) =>
        {
            var presented = context.HttpContext.Request.Headers[SyncHeaders.Token].ToString();
            if (!auth.IsSharedToken(presented)) return Results.Unauthorized();

            return await next(context);
        });

        // Creates a token for one device. The plaintext comes back once and is gone
        // from then on - which is the trade: the owner can no longer read a lost
        // device's token off the server, they revoke it and mint another.
        devices.MapPost("/", async Task<IResult> (
            MintDeviceRequest? body,
            DeviceTokenStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            var name = (body?.Name ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                return Results.BadRequest(
                    new { error = "A device needs a name. Something you will recognise in a list: 'work laptop', 'phone'." });
            }

            if (name.Length > MaxNameLength)
            {
                return Results.BadRequest(
                    new { error = $"That name is {name.Length} characters. Keep it under {MaxNameLength}." });
            }

            var (device, token) = await store.CreateAsync(name, ct);
            http.RequestAborted.ThrowIfCancellationRequested();

            return Results.Created($"/api/devices/{device.Id}", new MintedDevice(
                device.Id, device.Name, token, device.CreatedAt));
        });

        // Every device the owner has ever created, revoked ones included: a token
        // that disappears from the list is indistinguishable from one that was
        // never created, and "which laptop is that" needs an answer.
        devices.MapGet("/", async Task<IResult> (DeviceTokenStore store, CancellationToken ct) =>
        {
            var devices = await store.ListAsync(ct);
            return Results.Ok(devices.Select(device => new DeviceSummary(
                device.Id,
                device.Name,
                device.CreatedAt,
                device.LastSeenAt,
                device.IsRevoked)));
        });

        // Revoking is not deleting. The row stays so the list can say "this one was
        // revoked on Tuesday", and so a replayed token keeps being refused instead
        // of quietly becoming unknown.
        devices.MapDelete("/{id:guid}", async Task<IResult> (
            Guid id, DeviceTokenStore store, CancellationToken ct) =>
        {
            var device = await store.RevokeAsync(id, ct);
            return device is null ? Results.NotFound() : Results.NoContent();
        });

        return app;
    }
}
