using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Bitcoin.Tests.Bootstrap;

using Bitcoin.Bootstrap;
using Domain.Node.Bootstrap;
using Domain.Node.Options;
using Infrastructure.Protocol.Dns;

/// <summary>
/// The one live BOLT 10 test (NL-113): real DNS over TCP to 1.1.1.1, so it is Explicit and never runs by default
/// (the default run is hermetic, NL-168). Run it with
/// <c>dotnet run --project test/NLightning.Infrastructure.Bitcoin.Tests -f net10.0 -- -explicit only -trait Category=Live</c>.
/// </summary>
public class DnsSeedLiveTests
{
    [Fact(Explicit = true)]
    [Trait("Category", "Live")]
    public async Task Given_NodesLightningDirectory_When_QueriedOver1111_Then_ReturnsValidCandidates()
    {
        // Arrange
        var nodeOptions = new NodeOptions { Bootstrap = { NameServers = ["1.1.1.1", "8.8.8.8"] } };
        var options = Microsoft.Extensions.Options.Options.Create(nodeOptions);
        var lookup = new DnsClientRecordLookup(options, NullLogger<DnsClientRecordLookup>.Instance);
        var client = new DnsSeedClient(lookup, options, NullLogger<DnsSeedClient>.Instance);

        // Act
        var result = await client.QuerySeedAsync("nodes.lightning.directory", DnsSeedAddressTypes.Both, 25,
                                                 TestContext.Current.CancellationToken);

        // Assert
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{result.Outcome}: {result.Candidates.Count} candidates, {result.Rejected} rejected");
        foreach (var candidate in result.Candidates)
            TestContext.Current.TestOutputHelper?.WriteLine(candidate.ToPeerAddressInfo().Address);
        Assert.Equal(DnsSeedOutcome.Ok, result.Outcome);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.True(SeedAddressFilter.IsUsable(c.Address, c.Port, false, out _)));
    }
}