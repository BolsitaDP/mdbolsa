using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Server.Auth;
using DeviceSummary = MdBolsa.Server.Endpoints.DeviceSummary;
using MintedDevice = MdBolsa.Server.Endpoints.MintedDevice;
using MintDeviceRequest = MdBolsa.Server.Endpoints.MintDeviceRequest;

namespace MdBolsa.Server.Tests;

// Per-device tokens, end to end against the real server and the real database.
//
// The behaviour worth testing is not "does a token work" - it is the two rules
// that make the phase worth doing. A device token must NOT be able to mint or
// revoke devices, or one stolen laptop is permanent access and the phase has
// made nothing safer. And a revoked token must stop working immediately, without
// the owner having to touch every other device. Both are tested here rather than
// unit-tested because both are properties of the whole arrangement, and a unit
// test of the store would have passed against either.
public class DeviceTokenTests : IDisposable
{
    private const string BaseAddress = "http://localhost:5080/";
    private const string SharedToken = "dev-token-not-a-secret";

    private static string ConnectionString() => new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = 5432,
        Database = "mdbolsa_dev",
        Username = "mdbolsa",
        Password = "mdbolsa_dev",
    }.ConnectionString;

    private static readonly bool ServerAvailable = ProbeServer();

    // Every device this test mints is removed in Dispose, so a run leaves the
    // shared dev database with the same devices it found. The database is shared
    // with every other integration test (see IntegrationCollection), and a test
    // that tidies up after itself is the only kind that can run twice.
    private readonly List<Guid> _minted = [];

    public void Dispose()
    {
        if (!ServerAvailable || _minted.Count == 0) return;

        try
        {
            // Straight to the database, because the API deliberately only revokes -
            // a revoked row is meant to stay (see ADR 0014). That is right for an
            // owner and wrong for a test run, which would otherwise leave a growing
            // pile of "test phone" rows in a list a human is meant to read.
            using var connection = new Npgsql.NpgsqlConnection(ConnectionString());
            connection.Open();

            using var command = new Npgsql.NpgsqlCommand(
                "DELETE FROM device_tokens WHERE id = ANY(@ids)", connection);
            command.Parameters.AddWithValue("ids", _minted.ToArray());
            command.ExecuteNonQuery();
        }
        catch
        {
            // A cleanup failure must not turn a passing test red. A leftover device
            // row is revoked and inert.
        }
    }

    [Fact]
    public async Task Minted_token_works_for_the_api_and_survives_in_a_list()
    {
        if (!ServerAvailable) return;

        using var owner = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        owner.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var device = await MintAsync(owner, "test phone");
        Assert.StartsWith("mdb_", device.Token);

        // It is a real credential: the notes API answers for it.
        using var deviceClient = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        deviceClient.DefaultRequestHeaders.Add(SyncHeaders.Token, device.Token);

        var response = await deviceClient.GetAsync("api/notes/changes?since=2000-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And it shows up in the owner's list, under the name they gave it - which
        // is the entire interface an owner has for "which one was the laptop".
        var list = await ListAsync(owner);
        var found = Assert.Single(list.Where(entry => entry.Id == device.Id));
        Assert.Equal("test phone", found.Name);
        Assert.False(found.Revoked);
    }

    [Fact]
    public async Task The_shared_token_still_works()
    {
        if (!ServerAvailable) return;

        // The whole design rests on this. A client that was configured before this
        // phase must keep syncing, or "security" has become "nobody can sync".
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var response = await http.GetAsync("api/notes/changes?since=2000-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_device_token_cannot_mint_another_device()
    {
        if (!ServerAvailable) return;

        using var owner = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        owner.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var device = await MintAsync(owner, "test laptop");

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, device.Token);

        var mint = await http.PostAsJsonAsync("api/devices", new MintDeviceRequest("stolen laptop's new machine"));
        Assert.Equal(HttpStatusCode.Unauthorized, mint.StatusCode);

        var list = await http.GetAsync("api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        var revoke = await http.DeleteAsync($"api/devices/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, revoke.StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_stops_working_at_once()
    {
        if (!ServerAvailable) return;

        using var owner = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        owner.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var device = await MintAsync(owner, "test device to lose");

        using var lost = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        lost.DefaultRequestHeaders.Add(SyncHeaders.Token, device.Token);

        Assert.Equal(HttpStatusCode.OK,
            (await lost.GetAsync("api/notes/changes?since=2000-01-01T00:00:00Z")).StatusCode);

        var revoked = await owner.DeleteAsync($"api/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // Immediately, and without touching any other device. This is the property
        // the shared token could never have: the alternative was re-keying the house.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await lost.GetAsync("api/notes/changes?since=2000-01-01T00:00:00Z")).StatusCode);

        // The row is still listed, as revoked, rather than quietly disappearing -
        // "which of these was the phone I gave away" needs an answer.
        var entry = Assert.Single((await ListAsync(owner)).Where(item => item.Id == device.Id));
        Assert.True(entry.Revoked);
    }

    [Fact]
    public async Task A_revoked_token_cannot_upload_either()
    {
        if (!ServerAvailable) return;

        using var owner = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        owner.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var device = await MintAsync(owner, "test device to lose before uploading");
        await owner.DeleteAsync($"api/devices/{device.Id}");

        // Attachments are the reason this phase exists. A token that could still
        // upload after revocation would spend the owner's disk anyway.
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, device.Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, Guid.NewGuid().ToString());

        var bytes = System.Text.Encoding.UTF8.GetBytes("a revoked device should not be able to store this");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        using var content = new ByteArrayContent(bytes);
        var upload = await http.PutAsync($"api/attachments/{hash}", content);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);

        // And nothing was stored: refused before the body was read, not after.
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var check = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM attachment_blobs WHERE hash = @hash", connection);
        check.Parameters.AddWithValue("hash", hash);
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_device_with_no_name_is_refused_with_a_reason()
    {
        if (!ServerAvailable) return;

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);

        var response = await http.PostAsJsonAsync("api/devices", new MintDeviceRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A rejected request must not leave a device behind: an unnamed one in the
        // list would be a row the owner cannot act on.
        Assert.Empty((await ListAsync(http)).Where(entry => string.IsNullOrWhiteSpace(entry.Name)));
    }

    [Fact]
    public async Task A_token_that_is_not_a_device_token_is_rejected()
    {
        if (!ServerAvailable) return;

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, "mdb_" + new string('0', 32));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.GetAsync("api/notes/changes?since=2000-01-01T00:00:00Z")).StatusCode);
    }

    [Fact]
    public void A_minted_token_is_stored_only_as_a_hash()
    {
        // The property the whole scheme rests on, and the one that is cheapest to
        // check and easiest to break by accident in a future refactor.
        var token = DeviceTokenStore.NewToken();
        var hash = DeviceTokenStore.Hash(token);

        Assert.DoesNotContain(token, hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, DeviceTokenStore.Hash(token));
        Assert.NotEqual(hash, DeviceTokenStore.Hash(DeviceTokenStore.NewToken()));

        // The prefix is what lets the authenticator skip the database for anything
        // that obviously is not one of ours.
        Assert.True(DeviceTokenStore.LooksLikeDeviceToken(token));
        Assert.False(DeviceTokenStore.LooksLikeDeviceToken(SharedToken));
        Assert.False(DeviceTokenStore.LooksLikeDeviceToken(null));
    }

    // --- Plumbing -----------------------------------------------------------

    private async Task<MintedDevice> MintAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("api/devices", new MintDeviceRequest(name));
        response.EnsureSuccessStatusCode();

        var device = await response.Content.ReadFromJsonAsync<MintedDevice>();
        Assert.NotNull(device);
        _minted.Add(device.Id);

        return device;
    }

    private static async Task<List<DeviceSummary>> ListAsync(HttpClient owner)
    {
        var response = await owner.GetAsync("api/devices");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<DeviceSummary>>() ?? [];
    }

    private static bool ProbeServer()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Add(SyncHeaders.Token, SharedToken);
            return http.GetAsync("health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            // No server running. These are integration tests against a real one;
            // skipping is the established pattern here (see AttachmentIntegrationTests).
            return false;
        }
    }
}
