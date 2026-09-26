using MdBolsa.Server.Endpoints;
using MdBolsa.Server.Notes;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Environment and database wiring.
//
// Development/production isolation is the requirement vision.md §7 calls
// "extremely important", so it is enforced here rather than left to
// configuration discipline:
//
//   * The connection string comes from configuration, never from code.
//   * In Production the server REFUSES TO START without an explicit
//     connection string. A development build can't reach production by
//     accident, because pointing it at production requires setting a value
//     that has no default anywhere in this repo.
//   * The environment and the database host are logged at startup, so a
//     misconfigured deploy is obvious in the first three lines of output.
// ---------------------------------------------------------------------------
var environment = builder.Environment.EnvironmentName;
var connectionString = builder.Configuration.GetConnectionString("Postgres");

if (string.IsNullOrWhiteSpace(connectionString))
{
    if (environment == Environments.Production)
    {
        throw new InvalidOperationException(
            "No ConnectionStrings:Postgres configured. Production must be given an explicit " +
            "database connection string; there is deliberately no default to fall back to.");
    }

    // Development keeps a default so the daily loop stays one command, but it
    // points at a local throwaway database, never anything remote.
    connectionString = "Host=localhost;Port=5432;Database=mdbolsa_dev;Username=mdbolsa;" +
                       "Password=mdbolsa_dev;Pooling=true;Maximum Pool Size=20";
}

builder.Services.AddSingleton(new NoteStore(connectionString));
builder.Services.AddSingleton(new DatabaseInfo(
    environment,
    DescribeHost(connectionString),
    NoteSchema.Version));

var app = builder.Build();

var info = app.Services.GetRequiredService<DatabaseInfo>();
app.Logger.LogWarning(
    "mdbolsa server starting: environment={Environment} database={Host} schema={Schema} auth=none",
    info.Environment, info.Host, info.SchemaVersion);

app.MapGet("/health", (DatabaseInfo info) => Results.Ok(new
{
    status = "ok",
    environment = info.Environment,
    database = info.Host,
    schemaVersion = info.SchemaVersion,
    // Stated plainly rather than implied: vision.md §9 lists authentication
    // secrets as production configuration, and this phase has none.
    authentication = "none",
}));

app.MapNoteEndpoints();

// The schema is created on boot, not by a migration step (see NoteSchema).
// Failure here is fatal on purpose: a sync server that starts without its
// tables would fail every request later, far from the cause.
try
{
    await NoteSchema.InitialiseAsync(connectionString);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Could not initialise the database schema at {Host}.", info.Host);
    throw;
}

app.Run();

static string DescribeHost(string connectionString)
{
    // Never log the password: parse out just the host/database for the banner.
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    var host = parts.FirstOrDefault(p => p.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))?
        .Split('=', 2)[1] ?? "unknown";
    var database = parts.FirstOrDefault(p => p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase))?
        .Split('=', 2)[1] ?? "unknown";
    return $"{host}/{database}";
}

/// <summary>Startup facts worth logging, and worth exposing on /health.</summary>
public sealed record DatabaseInfo(string Environment, string Host, string SchemaVersion);
