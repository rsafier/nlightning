using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.Persistence;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Docker.Mock;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
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
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Serialization;

/// <summary>
/// Plan BOLT7 G2-T4 restart proof on SQLite (real migrations, unit of work and <c>GraphDbRepository</c>): the graph
/// built from the LND and CLN captures, written by the store's write-behind flush, is loaded by a new node into the
/// same snapshot, raw signed bytes included; and the G2-T5 pruner's spend and removal survive restarts.
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

    [Fact]
    public async Task Given_ASpentChannel_When_TheNodeRestartsAndBlocksPass_Then_ThePrunerRemovesItFromTheDatabase()
    {
        // Arrange (G2-T5): the captured graph, one channel's funding output spent at block 500
        var messages = Parse(Bolt7Vectors.Lnd.Concat(Bolt7Vectors.Cln));
        var now = DateTimeOffset.FromUnixTimeSeconds(messages.OfType<ChannelUpdateMessage>()
                                                             .Max(m => m.Payload.Timestamp) + 60);
        var ct = TestContext.Current.CancellationToken;
        var spentScid = messages.OfType<ChannelAnnouncementMessage>().First().Payload.ShortChannelId;
        int channelsBefore;
        await using (var node = await StartNodeAsync(now))
        {
            var ingress = node.GetRequiredService<GossipIngress>();
            var peer = new Mock<IPeerService>();
            peer.SetupGet(p => p.PeerPubKey).Returns(_keyManager.GetNodePubKey());
            foreach (var message in messages)
                await ingress.ProcessAsync(peer.Object, message, 0, ct);
            channelsBefore = node.GetRequiredService<IGraphStore>().ChannelCount;

            var monitor = node.GetRequiredService<Mock<IBlockchainMonitor>>();
            var pruner = node.GetRequiredService<GraphPruner>();
            pruner.Start();
            await pruner.WhenIdleAsync(ct);
            monitor.Raise(m => m.OnBlockInputs += null,
                          new BlockInputsEventArgs(500, Hash.Empty, [(TxIdFor(spentScid), spentScid.OutputIndex)]));
            await pruner.WhenIdleAsync(ct);
            await pruner.StopAsync();
        }

        // Act: after a restart (no funding txid in memory) the pruner looks them up, then block 572 passes
        uint? spentAfterRestart;
        await using (var node = await StartNodeAsync(now))
        {
            var store = node.GetRequiredService<IGraphStore>();
            var monitor = node.GetRequiredService<Mock<IBlockchainMonitor>>();
            var pruner = node.GetRequiredService<GraphPruner>();
            pruner.Start();
            await pruner.WhenIdleAsync(ct);
            spentAfterRestart = store.TryGetChannel(spentScid, out var spent) ? spent.SpentAtHeight : null;
            monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(571, Hash.Empty, []));
            monitor.Raise(m => m.OnBlockInputs += null, new BlockInputsEventArgs(572, Hash.Empty, []));
            await pruner.WhenIdleAsync(ct);
            await pruner.StopAsync();
        }

        IGraphView after;
        await using (var node = await StartNodeAsync(now))
        {
            var store = node.GetRequiredService<IGraphStore>();
            await store.LoadAsync(ct);
            after = store.GetSnapshot();
        }

        // Assert
        Assert.Equal(500u, spentAfterRestart);
        Assert.Equal(channelsBefore - 1, after.ChannelCount);
        Assert.False(after.TryGetChannel(spentScid, out _));
        Assert.All(after.Nodes, n => Assert.Contains(after.Channels, c => c.NodeId1 == n.NodeId
                                                                       || c.NodeId2 == n.NodeId));
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
              .ReturnsAsync((ShortChannelId scid, CompactPubKey _, CompactPubKey _, LightningMoney? _,
                             CancellationToken _) => Found(scid));
        lookup.Setup(l => l.LookupAsync(It.IsAny<ShortChannelId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ShortChannelId scid, CancellationToken _) => Found(scid));

        // The graph pruner follows a chain monitor the tests drive by hand
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(499);

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
        services.AddSingleton(monitor);
        services.AddSingleton(monitor.Object);
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static FundingOutputLookupResult Found(ShortChannelId shortChannelId) =>
        FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, TxIdFor(shortChannelId),
                                             LightningMoney.Satoshis(1_000_000), [0x00, 0x20], 6);

    /// <summary>A distinct funding txid per channel.</summary>
    private static TxId TxIdFor(ShortChannelId shortChannelId)
    {
        var bytes = new byte[32];
        ((byte[])shortChannelId).CopyTo(bytes, 0);
        bytes[31] = 0xAA;
        return new TxId(bytes);
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