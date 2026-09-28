using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Node.Bootstrap;

using Application.Gossip.Graph.Interfaces;
using Application.Node.Bootstrap;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Node.Bootstrap;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class PeerBootstrapServiceTests
{
    private const string SeedA = "nodes.lightning.directory";
    private const string SeedB = "nodes.lightning.wiki";

    private readonly Mock<IDnsSeedClient> _dnsSeedClient = new();
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISecureKeyManager> _secureKeyManager = new();
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly CapturingLogger _logger = new();
    private readonly ConcurrentDictionary<CompactPubKey, PeerModel> _connected = new();
    private readonly List<PeerModel> _saved = [];
    private readonly CompactPubKey _ourNodeId = NewKey();
    private IGraphStore? _graphStore;

    private readonly NodeOptions _nodeOptions = new()
    {
        BitcoinNetwork = BitcoinNetwork.Mainnet,
        Bootstrap =
        {
            Enabled = true,
            StartupDelay = TimeSpan.Zero,
            RetryInterval = TimeSpan.FromMilliseconds(1),
            MaxRuns = 1,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        }
    };

    public PeerBootstrapServiceTests()
    {
        _peerManager.Setup(p => p.ListPeers()).Returns(() => _connected.Values.ToList());
        _unitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => _saved.ToList());
        _secureKeyManager.Setup(k => k.GetNodePubKey()).Returns(() => _ourNodeId);
    }

    private static CompactPubKey NewKey() => new(new Key().PubKey.ToBytes());

    private static int s_nextAddress;

    /// <summary>A candidate; without an address each gets its own (one endpoint is dialed at most once).</summary>
    private static SeedPeerCandidate Candidate(string seed, CompactPubKey? nodeId = null, string? address = null,
                                               ushort port = 9735)
    {
        var n = Interlocked.Increment(ref s_nextAddress);
        return new SeedPeerCandidate(nodeId ?? NewKey(),
                                     IPAddress.Parse(address ?? $"11.{(n >> 16) & 0xff}.{(n >> 8) & 0xff}.{n & 0xff}"),
                                     port, seed);
    }

    private static DnsSeedResult Ok(string seed, params SeedPeerCandidate[] candidates) =>
        new(seed, DnsSeedOutcome.Ok, candidates, 0);

    private void SetupSeed(string seed, DnsSeedResult result) =>
        _dnsSeedClient.Setup(c => c.QuerySeedAsync(seed, It.IsAny<DnsSeedAddressTypes>(), It.IsAny<int>(),
                                                   It.IsAny<CancellationToken>()))
                      .ReturnsAsync(result);

    /// <summary>Every dial succeeds and the peer shows as connected.</summary>
    private void DialsSucceed() =>
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((PeerAddressInfo info, CancellationToken _) => Connect(info));

    private PeerModel Connect(PeerAddressInfo info)
    {
        var nodeId = new CompactPubKey(Convert.FromHexString(info.Address[..66]));
        var peer = new PeerModel(nodeId, "1.2.3.4", 9735, "IPv4");
        _connected[nodeId] = peer;
        return peer;
    }

    private PeerBootstrapService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        return new PeerBootstrapService(_dnsSeedClient.Object, _logger, Options.Create(_nodeOptions),
                                        _peerManager.Object, provider.GetRequiredService<IServiceScopeFactory>(),
                                        _secureKeyManager.Object, _graphStore, _blockchainMonitor.Object);
    }

    private async Task RunToEndAsync(PeerBootstrapService service)
    {
        await service.StartAsync(TestContext.Current.CancellationToken);
        if (service.Loop is not null)
            await service.Loop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private void VerifyNoSeedQuery() =>
        _dnsSeedClient.Verify(c => c.QuerySeedAsync(It.IsAny<string>(), It.IsAny<DnsSeedAddressTypes>(),
                                                    It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

    private int DialCount() =>
        _peerManager.Invocations.Count(i => i.Method.Name == nameof(IPeerManager.DialPeerAsync));

    [Fact]
    public async Task Given_BootstrapDisabled_When_Started_Then_NoSeedIsQueried()
    {
        // Arrange: explicitly off, also on mainnet
        _nodeOptions.Bootstrap.Enabled = false;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Null(service.Loop);
        VerifyNoSeedQuery();
        var status = service.GetStatus();
        Assert.False(status.Enabled);
        Assert.Null(status.StartedAt);
    }

    [Fact]
    public async Task Given_EnabledUnsetOnMainnet_When_Started_Then_TheSeedsAreQueried()
    {
        // Arrange: D-B10-1 as reversed by the owner on 2026-09-28
        _nodeOptions.Bootstrap.Enabled = null;
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        SetupSeed(SeedB, Ok(SeedB, Candidate(SeedB)));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.NotNull(service.Loop);
        Assert.Equal(2, _connected.Count);
        Assert.True(service.GetStatus().Enabled);
    }

    [Theory]
    [InlineData("testnet")]
    [InlineData("regtest")]
    [InlineData("signet")]
    public async Task Given_EnabledUnsetOffMainnet_When_Started_Then_NoSeedIsQueried(string network)
    {
        // Arrange: testnet has a seed but stays off by default
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Resolve(network);
        _nodeOptions.Bootstrap.Enabled = null;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Null(service.Loop);
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_ARun_When_ItEnds_Then_TheStatusRecordsItsSeedQueriesDialsAndEnd()
    {
        // Arrange: seed A answers through the fallback resolvers, seed B fails; one dial connects, one is refused
        _nodeOptions.Bootstrap.MinPeers = 1;
        var good = Candidate(SeedA);
        var refused = Candidate(SeedA);
        SetupSeed(SeedA, new DnsSeedResult(SeedA, DnsSeedOutcome.Ok, [good, refused], 2, UsedFallbackResolver: true,
                                           SystemResolverOutcome: DnsSeedOutcome.ServerFailure));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.NxDomain, [], 0));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns((PeerAddressInfo info, CancellationToken _) =>
                                 info == refused.ToPeerAddressInfo()
                                     ? Task.FromException<PeerModel>(new ConnectionException("refused"))
                                     : Task.FromResult(Connect(info)));
        _nodeOptions.Bootstrap.MaxDialConcurrency = 2;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        var status = service.GetStatus();
        Assert.True(status.Enabled);
        Assert.NotNull(status.StartedAt);
        Assert.NotNull(status.FinishedAt);
        Assert.Contains("MinPeers", status.EndReason);
        var run = Assert.Single(status.Runs);
        Assert.Null(run.SkipReason);
        Assert.Equal(1, run.Connected);
        Assert.Equal(1, run.PeersAfter);
        var queryA = Assert.Single(status.SeedQueries, q => q.Seed == SeedA);
        Assert.True(queryA.UsedFallbackResolver);
        Assert.Equal(DnsSeedOutcome.ServerFailure, queryA.SystemResolverOutcome);
        Assert.Equal(2, queryA.Candidates);
        Assert.Equal(2, queryA.Rejected);
        Assert.Contains(status.Dials, d => d.Candidate == good && d.Outcome == BootstrapDialOutcome.Connected);
        Assert.All(status.Dials.Where(d => d.Candidate == refused),
                   d => Assert.Equal(BootstrapDialOutcome.Failed, d.Outcome));
    }

    [Fact]
    public async Task Given_ASkippedRun_When_ItEnds_Then_TheStatusRecordsTheSkipReason()
    {
        // Arrange
        _blockchainMonitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        var status = service.GetStatus();
        var run = Assert.Single(status.Runs);
        Assert.Equal("chain processing is halted", run.SkipReason);
        Assert.Empty(status.SeedQueries);
        Assert.Contains("MaxRuns", status.EndReason);
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    public async Task Given_ANetworkWithoutSeedsAndConfiguredSeeds_When_Started_Then_NoQueryAndAWarning(
        string network)
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Resolve(network);
        _nodeOptions.Bootstrap.Seeds = [SeedA];
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ignored"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("no seeds"));
    }

    [Fact]
    public async Task Given_AllowSeedsOnThisNetwork_When_StartedOnRegtest_Then_TheConfiguredSeedIsQueried()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Regtest;
        _nodeOptions.Bootstrap.Seeds = ["seed.local.test"];
        _nodeOptions.Bootstrap.AllowSeedsOnThisNetwork = true;
        SetupSeed("seed.local.test", Ok("seed.local.test"));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        _dnsSeedClient.Verify(c => c.QuerySeedAsync("seed.local.test", DnsSeedAddressTypes.Both, 25,
                                                    It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ASavedPeerWithAnActiveChannelToReconnect_When_Started_Then_BootstrapIsSkipped()
    {
        // Arrange: the peer manager keeps reconnecting it
        _saved.Add(new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4") { Channels = [CreateChannel(ChannelState.Open)] });
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
        Assert.Contains(_logger.Entries, e => e.Message.Contains("saved peers with channels to reconnect"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ASavedDisconnectedPeerWithoutActiveChannels_When_Started_Then_BootstrapRuns(
        bool closedChannel)
    {
        // Arrange: an earlier bootstrap peer that went away; the peer manager dialed it once at start and never again
        _saved.Add(new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4")
        {
            Channels = closedChannel ? [CreateChannel(ChannelState.Closed)] : null
        });
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        SetupSeed(SeedB, Ok(SeedB, Candidate(SeedB)));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(2, _connected.Count);
    }

    [Fact]
    public async Task Given_OnlySavedPeersThatAreInboundOnlyOrConnected_When_Started_Then_BootstrapRuns()
    {
        // Arrange
        var connectedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
        _connected[connectedPeer.NodeId] = connectedPeer;
        _saved.Add(connectedPeer);
        _saved.Add(new PeerModel(NewKey(), string.Empty, 0, "IPv4") { IsInboundOnly = true });
        SetupSeed(SeedA, Ok(SeedA));
        SetupSeed(SeedB, Ok(SeedB));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        _dnsSeedClient.Verify(c => c.QuerySeedAsync(It.IsAny<string>(), It.IsAny<DnsSeedAddressTypes>(),
                                                    It.IsAny<int>(), It.IsAny<CancellationToken>()),
                              Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_EnoughConnectedPeers_When_Started_Then_BootstrapIsSkipped()
    {
        // Arrange
        for (var i = 0; i < _nodeOptions.Bootstrap.MinPeers; i++)
        {
            var peer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
            _connected[peer.NodeId] = peer;
        }

        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_AGraphWithNodeAddresses_When_Started_Then_BootstrapIsSkipped()
    {
        // Arrange
        var node = new GraphNode(NewKey(), 1, ReadOnlyMemory<byte>.Empty, new byte[32], new byte[3],
                                 [AddressDescriptor.FromIpAddress(IPAddress.Parse("1.2.3.4"), 9735)]);
        var view = new Mock<IGraphView>();
        view.Setup(v => v.Nodes).Returns(new[] { node });
        var store = new Mock<IGraphStore>();
        store.Setup(s => s.GetSnapshot()).Returns(view.Object);
        _graphStore = store.Object;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
        Assert.Contains(_logger.Entries, e => e.Message.Contains("graph"));
    }

    [Fact]
    public async Task Given_ChainProcessingHalted_When_Started_Then_BootstrapIsSkipped()
    {
        // Arrange
        _blockchainMonitor.Setup(m => m.IsChainProcessingHalted).Returns(true);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_AHaltThatClearsBeforeTheSecondRun_When_Bootstrapping_Then_TheSeedsAreQueriedThen()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 3;
        _blockchainMonitor.SetupSequence(m => m.IsChainProcessingHalted).Returns(true).Returns(false);
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 24).Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, Ok(SeedB, Enumerable.Range(0, 24).Select(_ => Candidate(SeedB)).ToArray()));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Single(_dnsSeedClient.Invocations);
        Assert.Equal(_nodeOptions.Bootstrap.MaxPeersFromBootstrap, _connected.Count);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("halted")
                                                                          && e.Message.Contains("checking again"));
    }

    [Fact]
    public async Task Given_AGateThatNeverClears_When_Bootstrapping_Then_ItIsCheckedMaxRunsTimesAndLoggedOnce()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 4;
        _blockchainMonitor.Setup(m => m.IsChainProcessingHalted).Returns(true);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        VerifyNoSeedQuery();
        _blockchainMonitor.Verify(m => m.IsChainProcessingHalted, Times.Exactly(4));
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("halted"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("after 4 runs"));
    }

    [Fact]
    public async Task Given_TheFirstSeedFails_When_Bootstrapping_Then_TheSecondIsQueriedAndItsPeersDialed()
    {
        // Arrange
        SetupSeed(SeedA, new DnsSeedResult(SeedA, DnsSeedOutcome.ServerFailure, [], 0));
        SetupSeed(SeedB, Ok(SeedB, Candidate(SeedB), Candidate(SeedB), Candidate(SeedB)));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        _dnsSeedClient.Verify(c => c.QuerySeedAsync(SeedA, It.IsAny<DnsSeedAddressTypes>(), It.IsAny<int>(),
                                                    It.IsAny<CancellationToken>()), Times.Once);
        _dnsSeedClient.Verify(c => c.QuerySeedAsync(SeedB, It.IsAny<DnsSeedAddressTypes>(), It.IsAny<int>(),
                                                    It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(3, _connected.Count);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(SeedA));
    }

    [Fact]
    public async Task Given_EnoughCandidatesFromOneSeed_When_Bootstrapping_Then_TheOtherSeedIsNotQueried()
    {
        // Arrange: MaxPeersFromBootstrap x 3 candidates from whichever seed is asked first
        var many = Enumerable.Range(0, 24).ToArray();
        SetupSeed(SeedA, Ok(SeedA, many.Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, Ok(SeedB, many.Select(_ => Candidate(SeedB)).ToArray()));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Single(_dnsSeedClient.Invocations);
        Assert.Equal(_nodeOptions.Bootstrap.MaxPeersFromBootstrap, _connected.Count);
    }

    [Fact]
    public async Task Given_FailingDials_When_Bootstrapping_Then_DialingGoesOnButNeverPastTheMaximum()
    {
        // Arrange: every other dial fails
        _nodeOptions.Bootstrap.MaxPeersFromBootstrap = 4;
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 20).Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.NxDomain, [], 0));
        var dials = 0;
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns((PeerAddressInfo info, CancellationToken _) => Interlocked.Increment(ref dials) % 2 == 0
                                                           ? Task.FromResult(Connect(info))
                                                           : Task.FromException<PeerModel>(
                                                               new ConnectionException("refused")));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(4, _connected.Count);
        Assert.InRange(DialCount(), 5, 9);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Given_TheSameNodeFromTwoSeeds_When_Bootstrapping_Then_ItIsDialedOnce()
    {
        // Arrange: a single-seed answer is too small to stop, so both seeds are asked
        var shared = NewKey();
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA, shared, "1.2.3.4"), Candidate(SeedA, shared, "5.6.7.8")));
        SetupSeed(SeedB, Ok(SeedB, Candidate(SeedB, shared, "9.9.9.9")));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(1, DialCount());
        Assert.Single(_connected);
    }

    [Fact]
    public async Task Given_CandidatesAlreadyConnectedSavedOrOurs_When_Bootstrapping_Then_TheyAreNotDialed()
    {
        // Arrange
        var connectedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
        _connected[connectedPeer.NodeId] = connectedPeer;
        var savedInbound = new PeerModel(NewKey(), string.Empty, 0, "IPv4") { IsInboundOnly = true };
        _saved.Add(savedInbound);
        var fresh = NewKey();
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA, connectedPeer.NodeId), Candidate(SeedA, savedInbound.NodeId),
                            Candidate(SeedA, _ourNodeId), Candidate(SeedA, fresh)));
        SetupSeed(SeedB, Ok(SeedB));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(1, DialCount());
        _peerManager.Verify(p => p.DialPeerAsync(It.Is<PeerAddressInfo>(i => i.Address.StartsWith(
                                                                                   fresh.ToString())),
                                                 It.IsAny<CancellationToken>()),
                            Times.Once);
    }

    [Fact]
    public async Task Given_TwoSeedsAnswering_When_Bootstrapping_Then_NoSeedSuppliesMoreThanItsShare()
    {
        // Arrange: 8 wanted from 2 seeds, so each may supply at most ceil(8 / 2) + 1 = 5; seed B has only 2
        var fromA = Enumerable.Range(0, 10).Select(_ => Candidate(SeedA)).ToArray();
        var fromB = new[] { Candidate(SeedB), Candidate(SeedB) };
        SetupSeed(SeedA, Ok(SeedA, fromA));
        SetupSeed(SeedB, Ok(SeedB, fromB));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(5, fromA.Count(c => _connected.ContainsKey(c.NodeId)));
        Assert.Equal(2, fromB.Count(c => _connected.ContainsKey(c.NodeId)));
        Assert.Equal(7, DialCount());
    }

    [Fact]
    public async Task Given_OnlyOneSeedAnswering_When_Bootstrapping_Then_ItMayFillTheWholeSet()
    {
        // Arrange
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 10).Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.Timeout, [], 0));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(_nodeOptions.Bootstrap.MaxPeersFromBootstrap, _connected.Count);
    }

    [Fact]
    public async Task Given_AlreadyConnectedAndRefusedDials_When_Bootstrapping_Then_TheyAreSkipped()
    {
        // Arrange
        var already = Candidate(SeedA);
        var refused = Candidate(SeedA);
        var good = Candidate(SeedA);
        SetupSeed(SeedA, Ok(SeedA, already, refused, good));
        SetupSeed(SeedB, Ok(SeedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns((PeerAddressInfo info, CancellationToken _) =>
                     {
                         if (info == already.ToPeerAddressInfo())
                             return Task.FromException<PeerModel>(new InvalidOperationException("Already connected"));
                         if (info == refused.ToPeerAddressInfo())
                             return Task.FromException<PeerModel>(new ConnectionException("refused"));
                         return Task.FromResult(Connect(info));
                     });
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(3, DialCount());
        Assert.Equal(good.NodeId, Assert.Single(_connected.Keys));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Given_ConcurrentDials_When_Bootstrapping_Then_TheLimitIsNeverExceeded()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxDialConcurrency = 3;
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 30).Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, Ok(SeedB));
        var inFlight = 0;
        var maxInFlight = 0;
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns(async (PeerAddressInfo info, CancellationToken _) =>
                     {
                         var now = Interlocked.Increment(ref inFlight);
                         lock (_connected)
                             maxInFlight = Math.Max(maxInFlight, now);
                         await Task.Delay(20);
                         Interlocked.Decrement(ref inFlight);
                         return Connect(info);
                     });
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(_nodeOptions.Bootstrap.MaxPeersFromBootstrap, _connected.Count);
        Assert.InRange(maxInFlight, 1, 3);
    }

    [Fact]
    public async Task Given_TheNodeStaysUnderMinPeers_When_Bootstrapping_Then_ItRetriesUpToMaxRuns()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 3;
        SetupSeed(SeedA, new DnsSeedResult(SeedA, DnsSeedOutcome.Empty, [], 0));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.NxDomain, [], 0));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(6, _dnsSeedClient.Invocations.Count);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("after 3 runs"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("connected no peer"));
    }

    [Fact]
    public async Task Given_ARunThatReachesMinPeers_When_Bootstrapping_Then_ItDoesNotRetry()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 3;
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 24).Select(_ => Candidate(SeedA)).ToArray()));
        SetupSeed(SeedB, Ok(SeedB, Enumerable.Range(0, 24).Select(_ => Candidate(SeedB)).ToArray()));
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Single(_dnsSeedClient.Invocations);
    }

    [Fact]
    public async Task Given_AnInFlightRun_When_Stopped_Then_ItIsCancelledPromptly()
    {
        // Arrange
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dnsSeedClient.Setup(c => c.QuerySeedAsync(It.IsAny<string>(), It.IsAny<DnsSeedAddressTypes>(),
                                                   It.IsAny<int>(), It.IsAny<CancellationToken>()))
                      .Returns(async (string seed, DnsSeedAddressTypes _, int _, CancellationToken ct) =>
                       {
                           queried.TrySetResult();
                           await Task.Delay(Timeout.Infinite, ct);
                           return Ok(seed);
                       });
        var service = CreateService();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await queried.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(service.Loop!.IsCompleted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Given_RoutineSeedFailures_When_Bootstrapping_Then_NothingIsLoggedAtError()
    {
        // Arrange
        SetupSeed(SeedA, new DnsSeedResult(SeedA, DnsSeedOutcome.Timeout, [], 0));
        _dnsSeedClient.Setup(c => c.QuerySeedAsync(SeedB, It.IsAny<DnsSeedAddressTypes>(), It.IsAny<int>(),
                                                   It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new System.Net.Sockets.SocketException());
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AnIgnoredObsoleteSeedList_When_Started_Then_AWarningOnlyWhenEnabled(bool enabled)
    {
        // Arrange
        _nodeOptions.Bootstrap.Enabled = enabled;
        _nodeOptions.Bootstrap.ObsoleteSeedsIgnored = true;
        _nodeOptions.Bootstrap.MinPeers = 1;
        var peer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
        _connected[peer.NodeId] = peer;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(enabled ? 1 : 0,
                     _logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("DnsSeedServers")));
    }

    [Fact]
    public async Task Given_ADialThatOutlivesTheConnectTimeout_When_Bootstrapping_Then_ItIsCancelledAndItsSlotReused()
    {
        // Arrange: one candidate hangs until its token is cancelled; the others connect. Three wanted from three
        // candidates, so every one is dialed whatever the shuffle
        _nodeOptions.Bootstrap.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        _nodeOptions.NetworkTimeout = TimeSpan.FromMilliseconds(50);
        _nodeOptions.Bootstrap.MaxDialConcurrency = 1;
        _nodeOptions.Bootstrap.MaxPeersFromBootstrap = 3;
        var slow = Candidate(SeedA, address: "1.1.1.1");
        SetupSeed(SeedA, Ok(SeedA, slow, Candidate(SeedA, address: "2.2.2.2"), Candidate(SeedA, address: "3.3.3.3")));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.NxDomain, [], 0));
        var cancelledDials = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns(async (PeerAddressInfo info, CancellationToken ct) =>
                     {
                         var now = Interlocked.Increment(ref inFlight);
                         lock (_connected)
                             maxInFlight = Math.Max(maxInFlight, now);
                         try
                         {
                             if (info == slow.ToPeerAddressInfo())
                             {
                                 try
                                 {
                                     await Task.Delay(Timeout.Infinite, ct);
                                 }
                                 catch (OperationCanceledException)
                                 {
                                     Interlocked.Increment(ref cancelledDials);
                                     throw;
                                 }
                             }

                             return Connect(info);
                         }
                         finally
                         {
                             Interlocked.Decrement(ref inFlight);
                         }
                     });
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert: the slow dial was cancelled (not abandoned) before the next one started
        Assert.Equal(1, cancelledDials);
        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, _connected.Count);
        Assert.DoesNotContain(slow.NodeId, _connected.Keys);
        var timedOut = Assert.Single(service.GetStatus().Dials, d => d.Candidate == slow);
        Assert.Equal(BootstrapDialOutcome.TimedOut, timedOut.Outcome);
    }

    [Fact]
    public async Task Given_ManyNodeIdsAtOneEndpoint_When_Bootstrapping_Then_ThatEndpointIsDialedOnce()
    {
        // Arrange: a seed points every node id at one third party
        SetupSeed(SeedA, Ok(SeedA, Enumerable.Range(0, 20).Select(_ => Candidate(SeedA, address: "9.9.9.9"))
                                             .ToArray()));
        SetupSeed(SeedB, Ok(SeedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ConnectionException("wrong node id"));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(1, DialCount());
    }

    [Fact]
    public async Task Given_AnEndpointThatFailed_When_TheNextRunComes_Then_ItIsNotDialedAgain()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 2;
        var failing = Candidate(SeedA, address: "9.9.9.9");
        SetupSeed(SeedA, Ok(SeedA, failing));
        SetupSeed(SeedB, Ok(SeedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ConnectionException("refused"));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert: two runs, each seed asked twice, the endpoint dialed once
        Assert.Equal(4, _dnsSeedClient.Invocations.Count);
        Assert.Equal(1, DialCount());
    }

    private ChannelModel CreateChannel(ChannelState state)
    {
        var channelIdBytes = new byte[32];
        Random.Shared.NextBytes(channelIdBytes);
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                                     LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, _ourNodeId, _ourNodeId, _ourNodeId, _ourNodeId, _ourNodeId,
                                            _ourNodeId);
        return new ChannelModel(channelConfig, new ChannelId(channelIdBytes), null, null, false, null, null,
                                LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0, _ourNodeId, 0,
                                state, ChannelVersion.V1);
    }

    private sealed class CapturingLogger : ILogger<PeerBootstrapService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}