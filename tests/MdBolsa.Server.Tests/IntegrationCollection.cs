using Xunit;

namespace MdBolsa.Server.Tests;

// The end-to-end sync tests share one running server, and therefore one database.
// Two of them writing concurrently would race on the "one note per path" index
// and on each other's cursors, so they run one at a time. Everything else in this
// project is free to run in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public class IntegrationCollection
{
    public const string Name = "sync-end-to-end";
}
