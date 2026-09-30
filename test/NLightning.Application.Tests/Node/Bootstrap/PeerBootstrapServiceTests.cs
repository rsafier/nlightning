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
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Dns;

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
    private TimeProvider? _clock;

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

        // Assisted location (NL-541): every seed answers the empty reply unless a test sets candidates up
        _dnsSeedClient.Setup(c => c.LocateNodeAsync(It.IsAny<string>(), It.IsAny<CompactPubKey>(),
                                                   It.IsAny<DnsSeedAddressTypes>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((string seed, CompactPubKey _, DnsSeedAddressTypes _, CancellationToken _) =>
                                        new DnsSeedNodeLocation(seed, DnsSeedOutcome.Empty, [], 0));
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

    private PeerBootstrapService CreateService(ITorDnsRecordLookup? torDnsLookup = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        return new PeerBootstrapService(_dnsSeedClient.Object, _logger, Options.Create(_nodeOptions),
                                        _peerManager.Object, provider.GetRequiredService<IServiceScopeFactory>(),
                                        _secureKeyManager.Object, _graphStore, _blockchainMonitor.Object, _clock,
                                        torDnsLookup);
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
        Assert.Null(service.Keeper);
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

    [Fact]
    public async Task Given_ANetworkWithoutSeedsAndConfiguredSeeds_When_Started_Then_NoQueryAndAWarning()
    {
        // Arrange (regtest is the only network without public seeds since NL-545)
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Regtest;
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

    #region Graph top-up (NL-543)

    private static uint NowUnix => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static uint DaysAgo(int days) => NowUnix - (uint)TimeSpan.FromDays(days).TotalSeconds;

    private static ulong s_nextScid;

    private readonly List<GraphChannel> _graphChannels = [];
    private readonly List<GraphNode> _graphNodes = [];

    /// <summary>
    /// An announced graph node with <paramref name="channels"/> channels to fresh nodes, each with an update of the
    /// node's own direction.
    /// </summary>
    private CompactPubKey AddGraphNode(string? address = null, ushort port = 9735, int channels = 2,
                                      uint? announcedAt = null, uint? updatedAt = null, bool disabled = false,
                                      AddressDescriptor? descriptor = null, CompactPubKey? nodeId = null)
    {
        var id = nodeId ?? NewKey();
        var n = Interlocked.Increment(ref s_nextAddress);
        AddressDescriptor[] addresses =
            descriptor is not null
                ? [descriptor]
                : [AddressDescriptor.FromIpAddress(
                    IPAddress.Parse(address ?? $"12.{(n >> 16) & 0xff}.{(n >> 8) & 0xff}.{n & 0xff}"), port)];
        _graphNodes.Add(new GraphNode(id, announcedAt ?? NowUnix, ReadOnlyMemory<byte>.Empty, new byte[32],
                                      new byte[3], addresses));
        for (var i = 0; i < channels; i++)
        {
            var other = NewKey();
            var idFirst = GraphChannel.CompareNodeIds(id, other) < 0;
            var (node1, node2) = idFirst ? (id, other) : (other, id);
            var channel = new GraphChannel(new ShortChannelId(Interlocked.Increment(ref s_nextScid)), node1, node2,
                                           node1, node2, 100_000);
            var flags = (byte)((idFirst ? 0 : 1) | (disabled ? ChannelUpdatePayload.ChannelFlagDisable : 0));
            _graphChannels.Add(channel.WithPolicy(new GraphPolicy(updatedAt ?? NowUnix, 1, flags, 40, 1_000,
                                                                  1_000_000_000, 1_000, 1)));
        }

        return id;
    }

    private void UseGraph()
    {
        var snapshot = new GraphSnapshot(_graphChannels, _graphNodes);
        var store = new Mock<IGraphStore>();
        store.Setup(s => s.GetSnapshot()).Returns(snapshot);
        _graphStore = store.Object;
    }

    private List<CompactPubKey> DialedNodeIds() =>
        _peerManager.Invocations.Where(i => i.Method.Name == nameof(IPeerManager.DialPeerAsync))
                    .Select(i => new CompactPubKey(
                                Convert.FromHexString(((PeerAddressInfo)i.Arguments[0]).Address[..66])))
                    .ToList();

    [Fact]
    public async Task Given_FewerPeersThanMinPeersAndAGraph_When_Bootstrapping_Then_GraphNodesAreDialedFirst()
    {
        // Arrange: the restart case of NL-543: a graph, no saved peer reachable
        var nodes = new[] { AddGraphNode(), AddGraphNode(), AddGraphNode() };
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert: MinPeers (3) reached from the graph alone, the seeds never asked
        Assert.Equal(nodes.ToHashSet(), _connected.Keys.ToHashSet());
        VerifyNoSeedQuery();
        var status = service.GetStatus();
        Assert.All(status.Dials, d => Assert.Equal(GraphPeerCandidateSelector.GraphSource, d.Candidate.Seed));
        var run = Assert.Single(status.Runs);
        Assert.Equal(3, run.GraphConnected);
        Assert.False(run.AskedSeeds);
        Assert.Contains("MinPeers", status.EndReason);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Given_UnusableGraphNodes_When_Bootstrapping_Then_OnlyTheUsableOneIsDialed()
    {
        // Arrange
        _nodeOptions.Bootstrap.AddressFamilies = DnsSeedAddressTypes.IPv4;
        SetupSeed(SeedA, Ok(SeedA));
        SetupSeed(SeedB, Ok(SeedB));
        var connectedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
        _connected[connectedPeer.NodeId] = connectedPeer;
        var good = AddGraphNode();
        AddGraphNode(nodeId: _ourNodeId); // ourselves
        AddGraphNode(nodeId: connectedPeer.NodeId); // already connected
        AddGraphNode(address: "10.1.2.3"); // private
        AddGraphNode(address: "127.0.0.1"); // loopback
        AddGraphNode(address: "8.8.4.4", port: 0); // port 0
        AddGraphNode(address: "2001:4860::1"); // IPv6, not asked for
        AddGraphNode(descriptor: AddressDescriptor.FromHost(AddressDescriptorType.TorV3,
                                                            new string('a', 56) + ".onion", 9735)); // Tor
        AddGraphNode(descriptor: AddressDescriptor.FromDnsHostname("node.example.com", 9735)); // hostname
        AddGraphNode(channels: 0); // no channel
        AddGraphNode(updatedAt: DaysAgo(30)); // stale updates only
        AddGraphNode(disabled: true); // disabled channels only
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(good, Assert.Single(DialedNodeIds()));
    }

    [Fact]
    public async Task Given_AGraphNodeWhoseDialFailed_When_TheNextRunComes_Then_ItIsNotDialedAgain()
    {
        // Arrange
        _nodeOptions.Bootstrap.MaxRuns = 2;
        AddGraphNode();
        UseGraph();
        SetupSeed(SeedA, Ok(SeedA));
        SetupSeed(SeedB, Ok(SeedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ConnectionException("refused"));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(1, DialCount());
        Assert.Equal(2, service.GetStatus().Runs.Count);
    }

    [Fact]
    public async Task Given_ManyGraphNodes_When_Bootstrapping_Then_OnlyThePeersMissingToMinPeersAreDialed()
    {
        // Arrange: one peer connected, MinPeers 3: two are missing
        var peer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
        _connected[peer.NodeId] = peer;
        for (var i = 0; i < 20; i++)
            AddGraphNode();
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(2, DialCount());
        Assert.Equal(3, _connected.Count);
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_ManyGraphNodes_When_Bootstrapping_Then_TheDialLimitsHold()
    {
        // Arrange: 10 missing, but at most 3 connections per run and 2 dials at a time; every other dial fails
        _nodeOptions.Bootstrap.MinPeers = 10;
        _nodeOptions.Bootstrap.MaxPeersFromBootstrap = 3;
        _nodeOptions.Bootstrap.MaxDialConcurrency = 2;
        for (var i = 0; i < 30; i++)
            AddGraphNode();
        UseGraph();
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        SetupSeed(SeedB, Ok(SeedB, Candidate(SeedB)));
        var inFlight = 0;
        var maxInFlight = 0;
        var dials = 0;
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns(async (PeerAddressInfo info, CancellationToken _) =>
                     {
                         var now = Interlocked.Increment(ref inFlight);
                         lock (_connected)
                             maxInFlight = Math.Max(maxInFlight, now);
                         try
                         {
                             await Task.Delay(5, TestContext.Current.CancellationToken);
                             if (Interlocked.Increment(ref dials) % 2 == 0)
                                 throw new ConnectionException("refused");
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

        // Assert: 3 connections from the graph fill the run's cap, so the seeds are not asked; never 3 dials at once
        Assert.Equal(3, _connected.Count);
        Assert.True(maxInFlight <= 2, $"{maxInFlight} dials at once");
        Assert.All(service.GetStatus().Dials,
                   d => Assert.Equal(GraphPeerCandidateSelector.GraphSource, d.Candidate.Seed));
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_GraphNodesThatDoNotAnswer_When_Bootstrapping_Then_TheSeedsAreAskedAndDialed()
    {
        // Arrange: every graph node refuses, the seeds' candidates connect
        _nodeOptions.Bootstrap.MinPeers = 2;
        var graphNodes = new[] { AddGraphNode(), AddGraphNode() };
        UseGraph();
        var seedA = Candidate(SeedA);
        var seedB = Candidate(SeedB);
        SetupSeed(SeedA, Ok(SeedA, seedA));
        SetupSeed(SeedB, Ok(SeedB, seedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .Returns((PeerAddressInfo info, CancellationToken _) =>
                                 graphNodes.Any(n => info.Address.StartsWith(n.ToString()))
                                     ? Task.FromException<PeerModel>(new ConnectionException("refused"))
                                     : Task.FromResult(Connect(info)));
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(new[] { seedA.NodeId, seedB.NodeId }.ToHashSet(), _connected.Keys.ToHashSet());
        Assert.All(graphNodes, n => Assert.Contains(n, DialedNodeIds()));
        var run = Assert.Single(service.GetStatus().Runs);
        Assert.True(run.AskedSeeds);
        Assert.Equal(2, run.GraphAttempted);
        Assert.Equal(0, run.GraphConnected);
        Assert.Equal(2, run.Connected);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Given_EnoughConnectedPeersAndAGraph_When_Started_Then_NothingIsDialed()
    {
        // Arrange
        for (var i = 0; i < _nodeOptions.Bootstrap.MinPeers; i++)
        {
            var peer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4");
            _connected[peer.NodeId] = peer;
        }

        AddGraphNode();
        UseGraph();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_ASavedPeerWithAnActiveChannelAndAGraph_When_Started_Then_NothingIsDialed()
    {
        // Arrange: the peer manager keeps reconnecting it
        _saved.Add(new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4") { Channels = [CreateChannel(ChannelState.Open)] });
        AddGraphNode();
        UseGraph();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
    }

    [Fact]
    public async Task Given_ANetworkWithoutSeedsButAGraph_When_Enabled_Then_GraphNodesAreDialed()
    {
        // Arrange: regtest has no seeds (NL-545); the graph still tops up
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Regtest;
        _nodeOptions.Bootstrap.MinPeers = 1;
        var node = AddGraphNode();
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(node, Assert.Single(_connected.Keys));
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_TorOnlyOnMainnet_When_Bootstrapping_Then_NoSeedIsAskedAndTheOnionIsDialedFirst()
    {
        // Arrange: without a Tor resolver the seeds have no way through Tor; a node with an IP and an onion is dialed
        // at its onion
        const string onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";
        _nodeOptions.Tor.Mode = TorMode.TorOnly;
        _nodeOptions.Bootstrap.MinPeers = 1;
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        var node = AddGraphNode(descriptor: AddressDescriptor.FromHost(AddressDescriptorType.TorV3, onion, 9735));
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(node, Assert.Single(_connected.Keys));
        var dialed = (PeerAddressInfo)Assert.Single(_peerManager.Invocations,
                                                    i => i.Method.Name == nameof(IPeerManager.DialPeerAsync))
                                           .Arguments[0];
        Assert.EndsWith($"@{onion}:9735", dialed.Address);
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_TorOnlyWithATorResolver_When_Bootstrapping_Then_TheSeedsAreAsked()
    {
        // Arrange - NL-571: the seeds go through Node:Bootstrap:TorNameServer over Tor's SOCKS port
        _nodeOptions.Tor.Mode = TorMode.TorOnly;
        _nodeOptions.Bootstrap.MinPeers = 1;
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        DialsSucceed();
        var torLookup = new Mock<ITorDnsRecordLookup>();
        torLookup.SetupGet(l => l.IsAvailable).Returns(true);
        var service = CreateService(torLookup.Object);

        // Act
        await RunToEndAsync(service);

        // Assert
        _dnsSeedClient.Verify(c => c.QuerySeedAsync(SeedA, It.IsAny<DnsSeedAddressTypes>(), It.IsAny<int>(),
                                                    It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_TorOffAndAnOnionOnlyGraphNode_When_Bootstrapping_Then_ItIsNotDialed()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Signet;
        _nodeOptions.Bootstrap.MinPeers = 1;
        AddGraphNode(descriptor: AddressDescriptor.FromHost(
                         AddressDescriptorType.TorV3, "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion",
                         9735));
        UseGraph();
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
    }

    [Fact]
    public async Task Given_BootstrapDisabledAndAGraph_When_Started_Then_NothingIsDialed()
    {
        // Arrange: the graph top-up follows Node:Bootstrap:Enabled
        _nodeOptions.Bootstrap.Enabled = false;
        AddGraphNode();
        UseGraph();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
    }

    #endregion

    #region Peer-count keeper (NL-547)

    private ManualClock UseManualClock()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        _clock = clock;
        return clock;
    }

    private void SeedsFindNothing()
    {
        SetupSeed(SeedA, new DnsSeedResult(SeedA, DnsSeedOutcome.Empty, [], 0));
        SetupSeed(SeedB, new DnsSeedResult(SeedB, DnsSeedOutcome.NxDomain, [], 0));
    }

    private void AddConnectedPeers(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var nodeId = NewKey();
            _connected[nodeId] = new PeerModel(nodeId, "1.2.3.4", 9735, "IPv4");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold within 10 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Given_PeersThatDropAfterTheInitialPhase_When_TheKeeperRuns_Then_ItTopsUpAgain()
    {
        // Arrange: the initial phase reaches MinPeers, then the peer goes away
        _nodeOptions.Bootstrap.MinPeers = 1;
        _nodeOptions.Bootstrap.MaintenanceInterval = TimeSpan.FromMilliseconds(20);
        _nodeOptions.Bootstrap.MaxMaintenanceBackoff = TimeSpan.FromMilliseconds(20);
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        SetupSeed(SeedB, Ok(SeedB));
        DialsSucceed();
        var service = CreateService();
        await RunToEndAsync(service);
        Assert.Single(_connected);
        Assert.Equal(1, DialCount());

        // Act
        _connected.Clear();
        await WaitUntilAsync(() => !_connected.IsEmpty);

        // Assert
        Assert.Equal(2, DialCount());
        var status = service.GetStatus();
        Assert.True(status.Maintaining);
        Assert.Contains("MinPeers", status.EndReason);
        Assert.Contains(status.Runs, r => r is { Maintenance: true, Connected: 1 });
        Assert.False(service.Keeper!.IsCompleted);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_EnoughPeers_When_TheKeeperChecks_Then_NothingIsDoneAndNothingIsRecorded()
    {
        // Arrange: MinPeers 3 connected
        UseGraph();
        AddConnectedPeers(3);
        var service = CreateService();

        // Act
        var check = await service.MaintainOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(MaintenanceCheck.EnoughPeers, check);
        VerifyNoSeedQuery();
        Assert.Equal(0, DialCount());
        Assert.Empty(service.GetStatus().Runs);
        Assert.Null(service.MaintenanceBackoff);
    }

    [Fact]
    public async Task Given_TopUpsThatFindNoPeer_When_TheKeeperChecks_Then_TheBackoffGrowsToItsCapAndResets()
    {
        // Arrange
        var clock = UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        SeedsFindNothing();
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert: 5, 10, 20, 40 minutes, then the 1 h cap
        TimeSpan[] expected =
        [
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40),
            TimeSpan.FromHours(1), TimeSpan.FromHours(1)
        ];
        foreach (var backoff in expected)
        {
            Assert.Equal(MaintenanceCheck.StillShort, await service.MaintainOnceAsync(ct));
            Assert.Equal(backoff, service.MaintenanceBackoff);
            Assert.Equal(clock.GetUtcNow() + backoff, service.GetStatus().NextTopUpAt);

            // Nothing before the backoff's time: no seed is asked
            var queries = _dnsSeedClient.Invocations.Count;
            clock.Advance(backoff - TimeSpan.FromSeconds(1));
            Assert.Equal(MaintenanceCheck.BackingOff, await service.MaintainOnceAsync(ct));
            Assert.Equal(queries, _dnsSeedClient.Invocations.Count);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // One warning per backoff step at zero peers, none again at the cap; never an error
        Assert.Equal(5, _logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("still no peer")));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("connected no peer"));

        // The peers come back: the backoff resets
        AddConnectedPeers(1);
        Assert.Equal(MaintenanceCheck.EnoughPeers, await service.MaintainOnceAsync(ct));
        Assert.Null(service.MaintenanceBackoff);
        Assert.Null(service.GetStatus().NextTopUpAt);

        // And they drop again: an immediate top-up, the backoff from its start
        _connected.Clear();
        Assert.Equal(MaintenanceCheck.StillShort, await service.MaintainOnceAsync(ct));
        Assert.Equal(TimeSpan.FromMinutes(5), service.MaintenanceBackoff);
        Assert.All(service.GetStatus().Runs, r => Assert.True(r.Maintenance));
    }

    [Fact]
    public async Task Given_ATopUpThatReachesMinPeers_When_TheKeeperChecks_Then_TheBackoffResets()
    {
        // Arrange: a first top-up finds nothing, the second a peer
        var clock = UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        SeedsFindNothing();
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(MaintenanceCheck.StillShort, await service.MaintainOnceAsync(ct));
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA)));
        DialsSucceed();
        clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        var check = await service.MaintainOnceAsync(ct);

        // Assert
        Assert.Equal(MaintenanceCheck.ToppedUp, check);
        Assert.Single(_connected);
        Assert.Null(service.MaintenanceBackoff);
    }

    [Fact]
    public async Task Given_AFailedEndpoint_When_ItsTtlPasses_Then_ItIsDialedAgain()
    {
        // Arrange
        var clock = UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        SetupSeed(SeedA, Ok(SeedA, Candidate(SeedA, address: "9.9.9.9")));
        SetupSeed(SeedB, Ok(SeedB));
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ConnectionException("refused"));
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert: dialed, then skipped within the TTL, then dialed again
        await service.MaintainOnceAsync(ct);
        Assert.Equal(1, DialCount());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(MaintenanceCheck.StillShort, await service.MaintainOnceAsync(ct));
        Assert.Equal(1, DialCount());
        clock.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal(MaintenanceCheck.StillShort, await service.MaintainOnceAsync(ct));
        Assert.Equal(2, DialCount());
    }

    [Fact]
    public async Task Given_AFailedGraphEndpoint_When_ItsTtlPasses_Then_ItIsDialedAgain()
    {
        // Arrange
        var clock = UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        _nodeOptions.Bootstrap.Seeds = [];
        AddGraphNode();
        UseGraph();
        _peerManager.Setup(p => p.DialPeerAsync(It.IsAny<PeerAddressInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ConnectionException("refused"));
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        // Act: failed at 0, skipped at 59 min, dialed again at 70 min (the backoff is 10 min by then)
        await service.MaintainOnceAsync(ct);
        clock.Advance(TimeSpan.FromMinutes(59));
        await service.MaintainOnceAsync(ct);
        var dialsWithinTtl = DialCount();
        clock.Advance(TimeSpan.FromMinutes(11));
        await service.MaintainOnceAsync(ct);

        // Assert
        Assert.Equal(1, dialsWithinTtl);
        Assert.Equal(2, DialCount());
    }

    [Fact]
    public void Given_MoreFailuresThanTheBound_When_Recorded_Then_TheOldestAreForgotten()
    {
        // Arrange
        var clock = UseManualClock();
        var service = CreateService();
        var start = clock.GetUtcNow();
        var endpoints = Enumerable.Range(0, PeerBootstrapService.MaxFailedEndpoints + 10)
                                  .Select(i => ($"13.{(i >> 16) & 0xff}.{(i >> 8) & 0xff}.{i & 0xff}", (ushort)9735))
                                  .ToList();

        // Act
        for (var i = 0; i < endpoints.Count; i++)
            service.RecordFailedEndpoint(endpoints[i], start + TimeSpan.FromSeconds(i));

        // Assert
        Assert.Equal(PeerBootstrapService.MaxFailedEndpoints, service.FailedEndpointCount);
        var failed = service.GetFailedEndpoints();
        Assert.All(endpoints.Take(10), e => Assert.DoesNotContain(e, failed));
        Assert.All(endpoints.Skip(10), e => Assert.Contains(e, failed));
    }

    [Fact]
    public void Given_ExpiredFailures_When_Read_Then_TheyAreForgotten()
    {
        // Arrange
        var clock = UseManualClock();
        var service = CreateService();
        service.RecordFailedEndpoint(("13.0.0.1", 9735), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(30));
        service.RecordFailedEndpoint(("13.0.0.2", 9735), clock.GetUtcNow());

        // Act
        clock.Advance(TimeSpan.FromMinutes(30));
        var failed = service.GetFailedEndpoints();

        // Assert
        Assert.Equal(("13.0.0.2", (ushort)9735), Assert.Single(failed));
        Assert.Equal(1, service.FailedEndpointCount);
    }

    [Fact]
    public async Task Given_ChainProcessingHalted_When_TheKeeperChecks_Then_TheTopUpIsSkippedWithoutBackoff()
    {
        // Arrange
        UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        _blockchainMonitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        var service = CreateService();

        // Act
        var check = await service.MaintainOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(MaintenanceCheck.Skipped, check);
        VerifyNoSeedQuery();
        Assert.Null(service.MaintenanceBackoff);
        var run = Assert.Single(service.GetStatus().Runs);
        Assert.True(run.Maintenance);
        Assert.Equal("chain processing is halted", run.SkipReason);
    }

    [Fact]
    public async Task Given_ASavedPeerWithAnActiveChannel_When_TheKeeperChecks_Then_TheTopUpIsSkipped()
    {
        // Arrange: the peer manager keeps reconnecting it
        UseManualClock();
        _nodeOptions.Bootstrap.MinPeers = 1;
        UseGraph();
        _saved.Add(new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4") { Channels = [CreateChannel(ChannelState.Open)] });
        var service = CreateService();

        // Act
        var check = await service.MaintainOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(MaintenanceCheck.Skipped, check);
        VerifyNoSeedQuery();
        Assert.Equal(0, DialCount());
    }

    [Fact]
    public async Task Given_TheInitialPhaseReachedMinPeers_When_Stopped_Then_TheKeeperStopsCleanly()
    {
        // Arrange: the default 5 min interval, so the keeper waits when it is stopped
        AddConnectedPeers(3);
        var service = CreateService();
        await RunToEndAsync(service);
        await WaitUntilAsync(() => service.GetStatus().Maintaining);

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(service.Keeper!.IsCompleted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
        Assert.False(service.GetStatus().Maintaining);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Given_NoSeedsAndNoGraph_When_TheInitialPhaseEnds_Then_NoKeeperRuns()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Regtest;
        var service = CreateService();

        // Act
        await RunToEndAsync(service);
        await service.Keeper!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(service.GetStatus().Maintaining);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    #endregion

    #region BOLT 10 assisted location (NL-541)

    /// <summary>What the fake seed answers when a node is located; no candidates means its empty reply.</summary>
    private void SetupLocate(string seed, CompactPubKey nodeId, params SeedPeerCandidate[] candidates) =>
        _dnsSeedClient.Setup(c => c.LocateNodeAsync(seed, nodeId, It.IsAny<DnsSeedAddressTypes>(),
                                                   It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new DnsSeedNodeLocation(seed,
                                                            candidates.Length > 0
                                                                ? DnsSeedOutcome.Ok
                                                                : DnsSeedOutcome.Empty,
                                                            candidates, 0));

    [Fact]
    public async Task Given_LocatePeer_When_ASeedKnowsTheNode_Then_TheEndpointsAreReturnedAndRecorded()
    {
        // Arrange: both seeds know the node at the same endpoint; the answer is deduped
        var node = NewKey();
        SetupLocate(SeedA, node, new SeedPeerCandidate(node, IPAddress.Parse("1.2.3.4"), 9735, SeedA));
        SetupLocate(SeedB, node, new SeedPeerCandidate(node, IPAddress.Parse("1.2.3.4"), 9735, SeedB));
        var service = CreateService();

        // Act
        var located = await service.LocatePeerAsync(node, TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(located);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), candidate.Address);
        Assert.Equal(node, candidate.NodeId);
        var status = service.GetStatus();
        Assert.Equal(2, status.SeedQueries.Count);
        Assert.All(status.SeedQueries, q => Assert.Equal(0, q.Run));
        Assert.All(status.SeedQueries, q => Assert.Equal(DnsSeedOutcome.Ok, q.Outcome));
    }

    [Fact]
    public async Task Given_LocatePeer_When_NoSeedKnowsTheNode_Then_AnEmptyListIsReturnedWithoutThrowing()
    {
        // Arrange
        var node = NewKey();
        SetupLocate(SeedA, node);
        SetupLocate(SeedB, node);
        var service = CreateService();

        // Act
        var located = await service.LocatePeerAsync(node, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(located);
    }

    [Fact]
    public async Task Given_LocatePeer_When_ASeedFails_Then_TheErrorIsRecordedAndTheOtherSeedIsStillAsked()
    {
        // Arrange
        var node = NewKey();
        _dnsSeedClient.Setup(c => c.LocateNodeAsync(SeedA, node, It.IsAny<DnsSeedAddressTypes>(),
                                                   It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("boom"));
        SetupLocate(SeedB, node, new SeedPeerCandidate(node, IPAddress.Parse("1.2.3.4"), 9735, SeedB));
        var service = CreateService();

        // Act
        var located = await service.LocatePeerAsync(node, TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(located);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), candidate.Address);
        var status = service.GetStatus();
        Assert.Contains(status.SeedQueries, q => q.Error == "boom" && q.Outcome == DnsSeedOutcome.Error);
        Assert.Contains(status.SeedQueries, q => q.Error is null && q.Outcome == DnsSeedOutcome.Ok);
    }

    [Fact]
    public async Task Given_LocatePeer_When_TheNetworkHasNoSeeds_Then_NoSeedIsAskedAndTheAnswerIsEmpty()
    {
        // Arrange
        _nodeOptions.BitcoinNetwork = BitcoinNetwork.Regtest;
        var service = CreateService();

        // Act
        var located = await service.LocatePeerAsync(NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(located);
        _dnsSeedClient.Verify(c => c.LocateNodeAsync(It.IsAny<string>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<DnsSeedAddressTypes>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_LocatePeer_InTorOnlyMode_Then_NoSeedIsAskedAndTheAnswerIsEmpty()
    {
        // Arrange: clearnet DNS would reveal the lookup (NL-542)
        _nodeOptions.Tor.Mode = TorMode.TorOnly;
        var service = CreateService();

        // Act
        var located = await service.LocatePeerAsync(NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(located);
        _dnsSeedClient.Verify(c => c.LocateNodeAsync(It.IsAny<string>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<DnsSeedAddressTypes>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_AReconnectingChannelPeerTheSeedLocated_When_TheRunIsSkipped_Then_TheAnswerIsDialed()
    {
        // Arrange: the peer manager keeps dialing the saved address; the seed answers where the node is now
        var savedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4")
        {
            Channels = [CreateChannel(ChannelState.Open)]
        };
        _saved.Add(savedPeer);
        SetupLocate(SeedA, savedPeer.NodeId,
                    new SeedPeerCandidate(savedPeer.NodeId, IPAddress.Parse("9.9.9.9"), 9735, SeedA));
        SetupLocate(SeedB, savedPeer.NodeId);
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert: the located endpoint was dialed (never the seeds' bootstrap query) and the peer is connected
        var dial = Assert.Single(_peerManager.Invocations,
                                 i => i.Method.Name == nameof(IPeerManager.DialPeerAsync));
        Assert.Equal($"{savedPeer.NodeId}@9.9.9.9:9735", ((PeerAddressInfo)dial.Arguments[0]).Address);
        Assert.Contains(savedPeer.NodeId, _connected.Keys);
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_AReconnectingPeerNoSeedKnows_When_TheRunIsSkipped_Then_NothingIsDialed()
    {
        // Arrange: the seeds answer the empty reply, so the skip stays harmless
        var savedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4")
        {
            Channels = [CreateChannel(ChannelState.Open)]
        };
        _saved.Add(savedPeer);
        SetupLocate(SeedA, savedPeer.NodeId);
        SetupLocate(SeedB, savedPeer.NodeId);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
        VerifyNoSeedQuery();
    }

    [Fact]
    public async Task Given_TheSeedAnswersTheSavedEndpoint_When_TheRunIsSkipped_Then_ItIsNotRedialed()
    {
        // Arrange: the peer manager is already dialing exactly that endpoint
        var savedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4")
        {
            Channels = [CreateChannel(ChannelState.Open)]
        };
        _saved.Add(savedPeer);
        SetupLocate(SeedA, savedPeer.NodeId,
                    new SeedPeerCandidate(savedPeer.NodeId, IPAddress.Parse("5.6.7.8"), 9735, SeedA));
        SetupLocate(SeedB, savedPeer.NodeId);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
    }

    [Fact]
    public async Task Given_TheSeedAnswersTheDefaultPort_When_TheSavedPortDiffers_Then_BothEndpointsAreDialed()
    {
        // Arrange: the live seeds answer the addresses but not the per-node SRV port, so the saved port is a guess
        // worth dialing on the located address too
        var savedPeer = new PeerModel(NewKey(), "5.6.7.8", 9835, "IPv4")
        {
            Channels = [CreateChannel(ChannelState.Open)]
        };
        _saved.Add(savedPeer);
        SetupLocate(SeedA, savedPeer.NodeId,
                    new SeedPeerCandidate(savedPeer.NodeId, IPAddress.Parse("9.9.9.9"), 9735, SeedA));
        SetupLocate(SeedB, savedPeer.NodeId);
        DialsSucceed();
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(2, DialCount());
        var dialed = _peerManager.Invocations.Where(i => i.Method.Name == nameof(IPeerManager.DialPeerAsync))
                                 .Select(i => ((PeerAddressInfo)i.Arguments[0]).Address);
        Assert.Contains($"{savedPeer.NodeId}@9.9.9.9:9735", dialed);
        Assert.Contains($"{savedPeer.NodeId}@9.9.9.9:9835", dialed);
    }

    [Fact]
    public async Task Given_AReconnectingPeer_When_ChainProcessingIsHalted_Then_NothingIsLocatedOrDialed()
    {
        // Arrange
        var savedPeer = new PeerModel(NewKey(), "5.6.7.8", 9735, "IPv4")
        {
            Channels = [CreateChannel(ChannelState.Open)]
        };
        _saved.Add(savedPeer);
        _blockchainMonitor.Setup(b => b.IsChainProcessingHalted).Returns(true);
        var service = CreateService();

        // Act
        await RunToEndAsync(service);

        // Assert
        Assert.Equal(0, DialCount());
        VerifyNoSeedQuery();
        _dnsSeedClient.Verify(c => c.LocateNodeAsync(It.IsAny<string>(), It.IsAny<CompactPubKey>(),
                                                    It.IsAny<DnsSeedAddressTypes>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    #endregion

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