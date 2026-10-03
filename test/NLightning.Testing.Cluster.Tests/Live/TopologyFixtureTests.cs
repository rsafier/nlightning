using System.Collections.Concurrent;
using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Kube;
using Cluster.Topology;

/// <summary>A CLN pair on <c>emptyDir</c>s (its tests never restart a node), kept warm for its collection.</summary>
public sealed class WarmClnPair : IClusterTopologyDefinition
{
    public static string Suite => "warm-cln-pair";

    public static void Configure(TopologyBuilder builder)
    {
        builder.Storage = NodeStorage.Ephemeral;
        builder.AddBitcoinCore("miner")
               .AddCln("alice")
               .AddCln("bob")
               .FundWallet("alice", 2_000_000)
               .AddChannel("alice", "bob", 1_000_000);
    }
}

[CollectionDefinition(Name)]
public sealed class WarmClnPairCollection : ICollectionFixture<ClusterTopologyFixture<WarmClnPair>>
{
    public const string Name = "warm-cln-pair";
}

/// <summary>
/// The warm topology (<see cref="ClusterTopologyFixture{TDefinition}"/>): every test of the collection gets the same
/// namespace and nodes, built once, and leaves them as the next test may find them (here: a channel balance that only
/// grows, so each test asserts its own delta).
/// </summary>
[Trait("Category", "Cluster")]
[Collection(WarmClnPairCollection.Name)]
public class TopologyFixtureTests(ClusterTopologyFixture<WarmClnPair> fixture) : IAsyncLifetime
{
    private const long PaymentMsat = 10_000_000;

    private static readonly ConcurrentDictionary<string, byte> s_namespaces = new(StringComparer.Ordinal);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    // The warm topology starts with the first test that runs, not when xunit creates the fixture (NL-800)
    public ValueTask InitializeAsync() => new(fixture.EnsureStartedAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Explicit = true)]
    public Task Given_TheWarmPair_When_TheFirstTestPays_Then_ItUsesTheCollectionsTopology() => PayAndCheckAsync("first");

    [Fact(Explicit = true)]
    public Task Given_TheWarmPair_When_AnotherTestPays_Then_ItFindsTheSameTopology() => PayAndCheckAsync("second");

    private async Task PayAndCheckAsync(string label)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var alice = fixture.Node("alice");
        var bob = fixture.Node("bob");
        var aliceId = await alice.GetNodeIdAsync(ct);
        var before = (await bob.ListChannelsAsync(ct)).Single(c => c.RemoteNodeId == aliceId).LocalBalanceMsat;
        if (s_namespaces.IsEmpty)
            foreach (var line in fixture.StartLog)
                Log(line);

        // Act
        var invoice = await bob.CreateInvoiceAsync(PaymentMsat, $"warm {label} {Guid.NewGuid():N}", ct);
        var paid = await alice.PayInvoiceAsync(invoice.Bolt11, ct);

        // Assert: paid, the delta (not an absolute balance), and one namespace for the whole collection
        Assert.True(paid.Succeeded, paid.FailureReason);
        var after = (await bob.ListChannelsAsync(ct)).Single(c => c.RemoteNodeId == aliceId).LocalBalanceMsat;
        Assert.Equal(before + PaymentMsat, after);
        s_namespaces.TryAdd(fixture.Run.Namespace, 0);
        Assert.Single(s_namespaces);
        Log($"{fixture.Run.Namespace} ({label}): topology started once in {fixture.StartTime.TotalSeconds:F1} s; "
          + $"this test ran in {watch.Elapsed.TotalSeconds:F1} s");
    }
}