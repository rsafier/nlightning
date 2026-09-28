using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// NL-497 across a restart, on a SQLite file with the real migrations, unit of work and repositories: a peer that
/// connected to us from <c>127.0.0.1</c> is saved as inbound-only, so at the next start its channel is registered
/// (memory and signer) before any connection, and the peer is never dialed. A channel whose peer was never saved (a
/// database from before the fix) is registered too.
/// </summary>
public sealed class PeerManagerInboundRestartTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nltg-nl497-{Guid.NewGuid():N}.db");
    private readonly CompactPubKey _peerId = new Key().PubKey.ToBytes();
    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Mock<ITcpService> _tcpService = new();
    private readonly Mock<IPeerServiceFactory> _peerServiceFactory = new();
    private readonly Mock<IPeerService> _peerService = new();
    private readonly Mock<ISecureKeyManager> _keyManager = new();

    public PeerManagerInboundRestartTests()
    {
        _peerService.SetupGet(p => p.PeerPubKey).Returns(_peerId);
        _peerService.SetupGet(p => p.Features).Returns(new FeatureOptions());
        _peerServiceFactory.Setup(f => f.CreateConnectingPeerAsync(It.IsAny<TcpClient>()))
                           .ReturnsAsync(_peerService.Object);
        _keyManager.Setup(k => k.GetNodePubKey()).Returns(new Key().PubKey.ToBytes());
        _tcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                   .ThrowsAsync(new InvalidOperationException("the peer must not be dialed"));
    }

    [Fact]
    public async Task Given_AnInboundPeerFromLoopbackWithAChannel_When_TheNodeRestarts_Then_ItsChannelIsRegisteredAndItIsNotDialed()
    {
        // Arrange: the first process accepts the peer's connection from 127.0.0.1 and stores the channel it opened
        await using (var provider = await StartProcessAsync())
        {
            var first = CreatePeerManager(provider);
            await first.StartAsync(TestContext.Current.CancellationToken);
            _tcpService.Raise(t => t.OnNewPeerConnected += null, _tcpService.Object,
                              new NewPeerConnectedEventArgs("127.0.0.1", 50123, new Mock<TcpClient>().Object));
            using (var cts = new CancellationTokenSource(s_timeout))
                while (await ReadPeerAsync(provider) is null)
                    await Task.Delay(10, cts.Token);
            await StoreChannelAsync(provider);
            await first.StopAsync();
        }

        // Act: the restart
        await using var restarted = await StartProcessAsync();
        var registered = new List<ChannelId>();
        _channelManager.Setup(cm => cm.RegisterExistingChannelAsync(It.IsAny<ChannelModel>()))
                       .Callback((ChannelModel c) => registered.Add(c.ChannelId))
                       .Returns(Task.CompletedTask);
        var second = CreatePeerManager(restarted);
        await second.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        var saved = await ReadPeerAsync(restarted);
        Assert.NotNull(saved);
        Assert.True(saved.IsInboundOnly);
        Assert.Equal(ChannelIdOf(0x71), Assert.Single(registered));
        _tcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Never);
        await second.StopAsync();
    }

    [Fact]
    public async Task Given_AChannelWhosePeerWasNeverSaved_When_TheNodeRestarts_Then_ItsChannelIsStillRegistered()
    {
        // Arrange: a database written before NL-497 (the loopback peer was dropped, its channel kept)
        await using (var provider = await StartProcessAsync())
            await StoreChannelAsync(provider);

        // Act
        await using var restarted = await StartProcessAsync();
        var registered = new List<ChannelId>();
        _channelManager.Setup(cm => cm.RegisterExistingChannelAsync(It.IsAny<ChannelModel>()))
                       .Callback((ChannelModel c) => registered.Add(c.ChannelId))
                       .Returns(Task.CompletedTask);
        var peerManager = CreatePeerManager(restarted);
        await peerManager.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelIdOf(0x71), Assert.Single(registered));
        _tcpService.Verify(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()), Times.Never);
        await peerManager.StopAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }
        }
    }

    private PeerManager CreatePeerManager(IServiceProvider provider) =>
        new(_channelManager.Object, new Mock<IChannelMemoryRepository>().Object,
            NullLogger<PeerManager>.Instance, _peerServiceFactory.Object, _keyManager.Object, _tcpService.Object,
            provider);

    private async Task<ServiceProvider> StartProcessAsync()
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={_databasePath}"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                   .MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private async Task<Domain.Node.Models.PeerModel?> ReadPeerAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PeerDbRepository
                          .GetByNodeIdAsync(_peerId);
    }

    /// <summary>An open channel with the peer, stored as the channel layer stores it.</summary>
    private async Task StoreChannelAsync(IServiceProvider provider)
    {
        var local = new ChannelKeySetModel(0, Pub(), Pub(), Pub(), Pub(), Pub(), Pub());
        var remote = new ChannelKeySetModel(0, Pub(), Pub(), Pub(), Pub(), Pub(), Pub());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), local.FundingCompactPubKey,
                                                  remote.FundingCompactPubKey,
                                                  new TxId(Enumerable.Repeat((byte)0x72, 32).ToArray()), 0);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(1_000),
                                                     LightningMoney.Satoshis(546), 483,
                                                     LightningMoney.Satoshis(500_000), 3, false,
                                                     LightningMoney.Satoshis(546), 144, FeatureSupport.No);
        var channel = new ChannelModel(channelParams, ChannelIdOf(0x71),
                                       new CommitmentNumber(remote.PaymentCompactBasepoint,
                                                            local.PaymentCompactBasepoint, new Sha256()),
                                       fundingOutput, false, null, null, LightningMoney.Zero, local, 0, 0,
                                       LightningMoney.Satoshis(1_000_000), remote, 0, _peerId, 0, ChannelState.Open,
                                       ChannelVersion.V1);

        using var scope = provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelDbRepository.AddAsync(channel);
        await unitOfWork.SaveChangesAsync();
    }

    private static CompactPubKey Pub() => new Key().PubKey.ToBytes();

    private static ChannelId ChannelIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());
}