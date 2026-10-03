namespace MdBolsa.Server.Tests;

// The local development stack the end-to-end tests talk to: Postgres
// (`docker compose up -d`) and the server (`dotnet run --project src/Server/MdBolsa.Server`).
internal static class DevStack
{
    public const string BaseAddress = "http://localhost:5080/";

    // Probed once per test run: asking per test with a 2-second timeout would add
    // half a minute of waiting to a suite that is supposed to be fast.
    public static readonly bool Available = Probe();

    // Set MDBOLSA_REQUIRE_SERVER=1 to turn "the stack isn't up" from a skip into a
    // failure. A release gate wants exactly that: green must mean it was tested.
    public static readonly bool Required =
        Environment.GetEnvironmentVariable("MDBOLSA_REQUIRE_SERVER") == "1";

    private static bool Probe()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return probe.GetAsync($"{BaseAddress}health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

// A [Fact] that needs the dev stack. Without it the test is reported as *skipped*,
// with the reason, instead of returning early and being counted as passed: a test
// that checked nothing must not look like a test that found nothing wrong.
//
// A missing local database is still not a bug in the sync code, and a suite that
// demands Docker is a suite nobody runs - hence skip by default, fail on request.
public sealed class RequiresServerFactAttribute : FactAttribute
{
    public RequiresServerFactAttribute()
    {
        if (!DevStack.Available && !DevStack.Required)
        {
            Skip = $"needs the dev stack at {DevStack.BaseAddress} " +
                   "(docker compose up -d, then dotnet run --project src/Server/MdBolsa.Server); " +
                   "set MDBOLSA_REQUIRE_SERVER=1 to fail instead of skip";
        }
    }
}
