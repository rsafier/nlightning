using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain.Mempool;

using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Anchors;
using Application.Onchain.Interfaces;
using Application.Onchain.Mempool;
using Application.Protocol.Factories;
using Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;
using Onchain;

/// <summary>
/// NL-381: the O8 <see cref="MempoolReactor"/> hands the peer's commitment of an <b>anchor</b> channel, seen in the
/// mempool, to the anchor CPFP (<see cref="IAnchorCpfpService.OnPeerCommitmentInMempool"/>), which bumps it through
/// our anchor on it; our own commitment and a channel without anchors are not handed over.
/// </summary>
public sealed class MempoolPeerCommitmentTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<RealSigningCommitmentPair> _pairs = [];
    private readonly Mock<IAnchorCpfpService> _anchors = new();

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PeersCommitmentInTheMempool_When_Seen_Then_HandedToTheAnchorCpfpOnlyWithAnchors(
        bool hasAnchors)
    {
        // Arrange: Bob's current commitment (with our HTLC) spends the funding output in the mempool
        var (reactor, pair, build) = Create(hasAnchors);
        var remote = pair.Alice.State.RemoteCommit;
        var peers = build(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);

        // Act
        var reaction = await reactor.HandleSpendAsync(FundingSpend(pair.Alice.Channel, peers),
                                                      TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendKind.RemoteCommit, reaction.FundingSpendKind);
        _anchors.Verify(a => a.OnPeerCommitmentInMempool(pair.Alice.Channel.ChannelId,
                                                         It.Is<SignedTransaction>(t => t.TxId == peers.TxId), false),
                        hasAnchors ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Given_OurOwnCommitmentInTheMempool_When_Seen_Then_NotHandedOver()
    {
        // Arrange
        var (reactor, pair, build) = Create(hasAnchors: true);
        var local = pair.Alice.State.LocalCommit;
        var ours = build(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        var reaction = await reactor.HandleSpendAsync(FundingSpend(pair.Alice.Channel, ours),
                                                      TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendKind.LocalCommit, reaction.FundingSpendKind);
        _anchors.Verify(a => a.OnPeerCommitmentInMempool(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>(),
                                                         It.IsAny<bool>()), Times.Never);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        foreach (var pair in _pairs)
            pair.Dispose();
    }

    private (MempoolReactor Reactor, RealSigningCommitmentPair Pair,
        Func<CommitmentSide, CommitmentSpec, ulong, CompactPubKey?, SignedTransaction> Build) Create(bool hasAnchors)
    {
        var pair = new RealSigningCommitmentPair(hasAnchors);
        _pairs.Add(pair);
        pair.Add(pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        pair.Settle(pair.Alice);
        var channel = pair.Alice.Channel;
        channel.UpdateCommitments(pair.Alice.State);

        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
               {
                   found = channel;
                   return id == channel.ChannelId;
               }));
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(400);

        var store = new OnchainTestStore();
        var unitOfWork = store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new Domain.Node.Options.NodeOptions()));
        services.AddSingleton(Options.Create(new OnchainOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(monitor.Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(memory.Object);
        services.AddSingleton(new Mock<IChannelErrorSender>().Object);
        services.AddSingleton(new Mock<IOnchainResolutionExecutor>().Object);
        services.AddSingleton<IOutpointWatcher>(monitor.Object);
        services.AddSingleton(new Mock<ISecretStorageServiceFactory>().Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        services.AddSingleton(_anchors.Object);
        services.AddOnchainMempoolServices();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        SignedTransaction Build(CommitmentSide side, CommitmentSpec spec, ulong number, CompactPubKey? point)
        {
            var factory = provider.GetRequiredService<ICommitmentTransactionModelFactory>();
            var builder = provider.GetRequiredService<ICommitmentTransactionBuilder>();
            var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
            var model = side == CommitmentSide.Local
                            ? factory.CreateCommitmentTransactionModel(channel, txSpec, CommitmentSide.Local, number)
                            : factory.CreateCommitmentTransactionModel(channel, txSpec, CommitmentSide.Remote, number,
                                                                       point);
            return builder.BuildWithOutputMap(model).Transaction;
        }

        return (provider.GetRequiredService<MempoolReactor>(), pair, Build);
    }

    private static MempoolSpendEventArgs FundingSpend(ChannelModel channel, SignedTransaction spend) =>
        new(channel.ChannelId, spend, channel.FundingOutput!.TransactionId!.Value, channel.FundingOutput.Index!.Value,
            false);
}