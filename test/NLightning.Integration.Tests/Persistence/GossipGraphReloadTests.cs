using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.Persistence;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Docker.Mock;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure;
using Infrastructure.Bitcoin;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Serialization;

/// <summary>
/// Plan BOLT7 G2-T4 restart proof on SQLite (real migrations, unit of work and <c>GraphDbRepository</c>): the graph
/// built from the LND and CLN captures, written by the store's write-behind flush, is loaded by a new node into the
/// same snapshot, raw signed bytes included.
/// </summary>
public sealed class GossipGraphReloadTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-gossip-graph-{Guid.NewGuid():N}.db");

    private readonly FakeSecureKeyManager _keyManager = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Given_GraphFromCapturedGossip_When_TheNodeRestarts_Then_TheReloadedGraphEqualsTheOldOne()
    {
        // Arrange
        var messages = Parse(Bolt7Vectors.Lnd.Concat(Bolt7Vectors.Cln));
        var now = DateTimeOffset.FromUnixTimeSeconds(messages.OfType<ChannelUpdateMessage>()
                                                             .Max(m => m.Payload.Timestamp) + 60);
        IGraphView before;
        await using (var node = await StartNodeAsync(now))
        {
            var ingress = node.GetRequiredService<GossipIngress>();
            var peer = new Mock<IPeerService>();
            peer.SetupGet(p => p.PeerPubKey).Returns(_keyManager.GetNodePubKey());
            foreach (var message in messages)
                await ingress.ProcessAsync(peer.Object, message, 0, TestContext.Current.CancellationToken);

            var store = node.GetRequiredService<IGraphStore>();
            before = store.GetSnapshot();

            // Act
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        IGraphView after;
        await using (var node = await StartNodeAsync(now))
        {
            var store = node.GetRequiredService<IGraphStore>();
            await store.LoadAsync(TestContext.Current.CancellationToken);
            after = store.GetSnapshot();
        }

        // Assert: 4 announced channels with 7 policies and 3 nodes, reloaded byte for byte
        Assert.Equal(4, before.ChannelCount);
        Assert.Equal(7, before.Channels.Sum(c => (c.Policy1 is null ? 0 : 1) + (c.Policy2 is null ? 0 : 1)));
        Assert.Equal(3, before.Nodes.Count());
        Assert.Equal(before.ChannelCount, after.ChannelCount);
        foreach (var channel in before.Channels)
        {
            Assert.True(after.TryGetChannel(channel.ShortChannelId, out var reloaded));
            Assert.Equal(channel with { Policy1 = null, Policy2 = null },
                         reloaded with { Policy1 = null, Policy2 = null });
            Assert.Equal(channel.RawAnnouncement.ToArray(), reloaded.RawAnnouncement.ToArray());
            AssertSamePolicy(channel.Policy1, reloaded.Policy1);
            AssertSamePolicy(channel.Policy2, reloaded.Policy2);
        }

        Assert.Equal(before.Nodes.Count(), after.Nodes.Count());
        foreach (var node in before.Nodes)
        {
            Assert.True(after.TryGetNode(node.NodeId, out var reloaded));
            Assert.Equal(node, reloaded);
            Assert.Equal(node.RawAnnouncement.ToArray(), reloaded.RawAnnouncement.ToArray());
        }
    }

    private static void AssertSamePolicy(GraphPolicy? expected, GraphPolicy? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected with { RawUpdate = default, ExtraData = default },
                     actual with { RawUpdate = default, ExtraData = default });
        Assert.Equal(expected.RawUpdate.ToArray(), actual.RawUpdate.ToArray());
    }

    private async Task<ServiceProvider> StartNodeAsync(DateTimeOffset now)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={_databasePath}"
                           })
                           .Build();

        // Every funding output is found 6 deep (the lookup itself is proven in Infrastructure.Bitcoin.Tests)
        var lookup = new Mock<IFundingOutputLookup>();
        lookup.Setup(l => l.VerifyAsync(It.IsAny<ShortChannelId>(), It.IsAny<CompactPubKey>(),
                                        It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney?>(),
                                        It.IsAny<CancellationToken>()))
              .ReturnsAsync(FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, new byte[32],
                                                                 LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 6));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureKeyManager>(_keyManager);
        services.AddSingleton(lookup.Object);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
                                  new NodeOptions { BitcoinNetwork = BitcoinNetwork.Resolve("regtest") }));
        services.AddInfrastructureServices();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddGossipGraphServices();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static List<IMessage> Parse(IEnumerable<Bolt7CapturedMessage> vectors) =>
        vectors.Where(v => v.Type != 259)
               .Select(v => v.Type switch
                {
                    256 => (IMessage)new ChannelAnnouncementMessage(ChannelAnnouncementPayload.Parse(v.Payload)),
                    257 => new NodeAnnouncementMessage(NodeAnnouncementPayload.Parse(v.Payload)),
                    _ => new ChannelUpdateMessage(ChannelUpdatePayload.Parse(v.Payload))
                })
               .ToList();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}