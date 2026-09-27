using System.Net;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;

namespace MdBolsa.Core.Tests.Sync;

// The client half of per-device tokens (ADR 0014).
//
// These exist because the server half was fully tested and the client half was
// not, and the client half was the broken one. `DeviceTokenClient` deserialised
// the server's camelCase JSON with default options, which are case-sensitive:
// nothing matched, no exception was thrown, and the app received a record of
// default values. The visible symptom was a status line reading `This device is
// now ""` and - worse - a null token saved over the working one the app already
// had, so the app locked itself out of its own server.
//
// A bug that returns a plausible wrong answer instead of failing is exactly the
// kind a test has to be written against. The assertions here are about the parsed
// values, not about the request being made.
public class DeviceTokenClientTests
{
    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task Minting_parses_the_camelCase_the_server_actually_sends()
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;

        // Exactly the shape ASP.NET produces for the endpoint: camelCase keys, and
        // nothing else.
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://server/api/devices", request.RequestUri!.ToString());

            return Json(new
            {
                id,
                name = "work laptop",
                token = "mdb_0123456789abcdef0123456789abcdef",
                createdAt = created,
            });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://server/") };
        var client = new DeviceTokenClient(http, "shared");

        var minted = await client.MintAsync("work laptop");

        Assert.Equal(id, minted.Id);
        Assert.Equal("work laptop", minted.Name);
        Assert.Equal("mdb_0123456789abcdef0123456789abcdef", minted.Token);
        Assert.Equal(created, minted.CreatedAt.ToUniversalTime());
    }

    [Fact]
    public async Task The_request_carries_the_shared_token_and_the_name_asked_for()
    {
        string? seenToken = null;
        var handler = new StubHandler(request =>
        {
            seenToken = request.Headers.GetValues(SyncHeaders.Token).Single();
            return Json(new { id = Guid.NewGuid(), name = "phone", token = "mdb_abc", createdAt = DateTimeOffset.UtcNow });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://server/") };
        await new DeviceTokenClient(http, "the-shared-one").MintAsync("phone");

        Assert.Equal("the-shared-one", seenToken);
    }

    [Fact]
    public async Task Listing_parses_revoked_and_last_seen()
    {
        var id = Guid.NewGuid();
        var seen = DateTimeOffset.UtcNow.AddHours(-2);

        // One shape for both rows, so the array is well typed and each row is spelled out.
        object[] rows =
        {
            new { id, name = "phone", createdAt = DateTimeOffset.UtcNow, lastSeenAt = (DateTimeOffset?)seen, revoked = true },
            new { id = Guid.NewGuid(), name = "laptop", createdAt = DateTimeOffset.UtcNow, lastSeenAt = (DateTimeOffset?)null, revoked = false },
        };

        var handler = new StubHandler(_ => Json(rows));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://server/") };
        var devices = await new DeviceTokenClient(http, "shared").ListAsync();

        Assert.Equal(2, devices.Count);

        // The list is the only interface an owner has for "which of these was the
        // laptop I lost", so a revoked row and a never-seen row both have to
        // survive the parse distinctly.
        Assert.True(devices[0].Revoked);
        Assert.Equal(seen, devices[0].LastSeenAt);
        Assert.False(devices[1].Revoked);
        Assert.Null(devices[1].LastSeenAt);
    }

    [Fact]
    public async Task Being_refused_for_using_a_device_token_says_so()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(string.Empty),
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://server/") };
        var error = await Assert.ThrowsAsync<SyncException>(
            () => new DeviceTokenClient(http, "mdb_something").MintAsync("phone"));

        // The likeliest cause of a 401 here is the person pressing the button with
        // a device token already in the box, and the fix is to paste the shared one.
        // "Unauthorized" on its own would leave them stuck.
        Assert.Contains("shared token", error.Message);
    }

    [Fact]
    public void The_default_device_name_is_never_blank()
    {
        // The name goes into a list a human reads to decide what to revoke. An empty
        // row there is a credential nobody can act on.
        Assert.False(string.IsNullOrWhiteSpace(DeviceName.ForThisMachine()));
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, Camel), System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
