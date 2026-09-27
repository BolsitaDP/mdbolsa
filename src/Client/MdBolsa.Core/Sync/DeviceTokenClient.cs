using System.Net;
using System.Text.Json;
using MdBolsa.Contracts;

namespace MdBolsa.Core.Sync;

/// <summary>A freshly minted device token. The only time the secret is ever available.</summary>
public sealed record MintedDeviceToken(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);

/// <summary>One row of the device list. No token, because the server cannot produce one.</summary>
public sealed record DeviceTokenSummary(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    bool Revoked);

/// <summary>
/// Swaps the shared token for one of this device's own.
///
/// The flow it exists to support, in full: the owner starts the server, pastes the
/// shared token into settings once, and presses a button. The app calls
/// <see cref="MintAsync"/>, gets back a token that only this installation can use,
/// saves that instead, and the shared token is never written to disk again. From
/// then on losing this machine means revoking one row in a list, not re-keying
/// every device in the house.
///
/// The device name defaults to something a person would recognise in a list. This
/// matters more than it looks: the list is the only interface an owner has for
/// "which of these is the laptop I lost", and "DESKTOP-7K2Q1P" is a worse answer
/// than "work laptop" only if nobody typed the better one.
/// </summary>
public sealed class DeviceTokenClient(HttpClient http, string sharedToken)
{
    // The server's JSON is camelCase, and these records are PascalCase, so the
    // deserialiser has to be told. Without this it does not throw - it matches
    // nothing and quietly hands back a record of default values, so `Name` is
    // null and `Token` is null, and the app saves a null token over the good one
    // it already had. A silent wrong answer is worse than an exception here, and
    // this exact omission cost one build cycle: the button worked, the server
    // recorded the device, and the app threw its own credentials away.
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public async Task<MintedDeviceToken> MintAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Post, "api/devices", new { name = deviceName }, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, body);

        var minted = JsonSerializer.Deserialize<MintedDeviceToken>(body, Json);
        return minted ?? throw new SyncException("The server accepted the request but sent nothing back.");
    }

    public async Task<IReadOnlyList<DeviceTokenSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "api/devices", null, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, body);

        return JsonSerializer.Deserialize<List<DeviceTokenSummary>>(body, Json) ?? [];
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Delete, $"api/devices/{id}", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);

        // Always the shared token, never a device token: these are owner actions.
        request.Headers.Add(SyncHeaders.Token, sharedToken);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
        }

        return await http.SendAsync(request, cancellationToken);
    }

    private static SyncException Failure(HttpStatusCode status, string body) => status switch
    {
        // The likeliest cause by far, and the one with an obvious fix: this button
        // only works with the shared token, and the box probably holds a device
        // token from last time. Saying so is more use than "unauthorized".
        HttpStatusCode.Unauthorized =>
            new SyncException("The server refused that. Creating and revoking devices needs the shared " +
                              "token, not a device token - paste the shared token again."),

        HttpStatusCode.BadRequest when body.Contains("needs a name", StringComparison.OrdinalIgnoreCase) =>
            new SyncException("Give the device a name first - something you'd recognise in a list."),

        _ => new SyncException($"The server returned {(int)status} {status}. {body}".Trim()),
    };
}

/// <summary>
/// A name for this machine, used as the default device name.
///
/// "DESKTOP-7K2Q1P" is what the machine calls itself and it is stable, which is
/// the important part: a name that changed on every launch would make the device
/// list useless. Prefixed so it is obvious in a list that mixed which is which.
/// </summary>
public static class DeviceName
{
    public static string ForThisMachine() => Environment.MachineName.Trim() is { Length: > 0 } name
        ? name
        : "this device";
}
