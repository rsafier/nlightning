using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Application.Protocol.Factories;
using Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// NL-965 review (taproot wave t03, lane RVS): <see cref="OnchainChannelWatcher"/> over <b>real</b> simple taproot
/// commitments (<see cref="RealSigningCommitmentPair"/> with MuSig2, Alice's view) of a channel with a pending splice:
/// our commitment and the peer's on the splice funding (the same numbers, SP-I4) are classified against that funding and
/// their taproot outputs mapped with the splice's balances, as <c>OnchainSpliceWatcherTests</c> proves for P2WSH.
/// </summary>
public sealed class OnchainSpliceWatcherTaprootTests : IDisposable
{
    private const uint SpendHeight = 600;
    private const long SpliceInMsat = 250_000_000;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: true, simpleTaproot: true);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IOnchainResolutionExecutor> _executor = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly Mock<IChannelFundingDbRepository> _fundings = new();
    private readonly Mock<IRevokedCommitmentDbRepository> _revocationLog = new();
    private readonly Mock<ISpliceCommitmentBroadcaster> _spliceBroadcaster = new();
    private readonly List<ChannelFunding> _storedFundings = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainSpliceWatcherTaprootTests()
    {
        // Arrange (shared): an HTLC each way, committed on both sides
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Add(_pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()))
                    .ReturnsAsync(true);
        _fundings.Setup(f => f.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                 .ReturnsAsync(() => _storedFundings.ToList());

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundings.Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(_revocationLog.Object);
        _store.LoadChannel = id => id == _channel.ChannelId ? _pair.Bob.Channel : null;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new Domain.Node.Options.NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton(_errorSender.Object);
        services.AddSingleton(_executor.Object);
        services.AddSingleton(_outpointWatcher.Object);
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton(_spliceBroadcaster.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        _provider = services.BuildServiceProvider();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    private ChannelFunding Current => ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;

    [Fact]
    public async Task Given_OurTaprootCommitmentOnThePendingSplice_When_ItConfirms_Then_LocalCloseMappedOnThatFunding()
    {
        // Arrange: the splice confirmed and our commitment on it (same number, SP-I4) spends its output
        var splice = AddPendingSplice(TxIdOf(0xC7), 1);
        var local = _pair.Alice.State.LocalCommit;
        var spec = ChannelCommitments.SpecFor(local.Spec, splice);
        _fundings.Setup(f => f.GetLocalCommitmentAsync(_channel.ChannelId, splice.FundingTxId))
                 .ReturnsAsync(new LocalCommit(local.Number, spec, local.RemoteSignatures));
        var spend = BuildCommitment(CommitmentSide.Local, spec, local.Number, null, splice);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, splice),
                                                            TestContext.Current.CancellationToken);

        // Assert: to_local (with the splice's extra 250,000 sat, a P2TR output with its delay leaf) and both HTLCs
        Assert.NotNull(outcome);
        Assert.Equal(ChannelCloseKind.LocalCommitment, outcome.Kind);
        var close = Assert.Single(_store.Closes.Values);
        Assert.Equal(spend.TxId, close.CommitmentTransactionId);
        Assert.Equal(local.Number, close.CommitmentNumber);
        var kinds = _store.Outputs.Values.Select(o => o.Descriptor).ToList();
        Assert.Contains(OutputDescriptorKind.LocalOfferedHtlc, kinds);
        Assert.Contains(OutputDescriptorKind.LocalReceivedHtlc, kinds);
        Assert.All(_store.Outputs.Values, o => Assert.Equal(spend.TxId, o.TransactionId));
        var toLocal = OutputDescriptorData.Decode(_store.Outputs.Values
                                                        .Single(o => o.Descriptor == OutputDescriptorKind.DelayedToLocal)
                                                        .DescriptorData);
        Assert.Equal([0x51, 0x20], toLocal.ScriptPubKey[..2]);
        Assert.NotNull(toLocal.TaprootControlBlock);
        var onCurrent = (ulong)_pair.Alice.State.LocalCommit.Spec.LocalMsat / 1_000;
        Assert.True(toLocal.AmountSat > onCurrent, "our to_local must carry the splice-in");
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_ThePeersTaprootCommitmentOnThePendingSplice_When_ItConfirms_Then_RemoteCloseOnThatFunding()
    {
        // Arrange
        var splice = AddPendingSplice(TxIdOf(0xC8), 1);
        var remote = _pair.Alice.State.RemoteCommit;
        var spec = ChannelCommitments.SpecFor(remote.Spec, splice);
        _fundings.Setup(f => f.GetRemoteCommitmentAsync(_channel.ChannelId, splice.FundingTxId))
                 .ReturnsAsync((remote with { Spec = spec }, (CommitmentSignatures?)null));
        var spend = BuildCommitment(CommitmentSide.Remote, spec, remote.Number, remote.PerCommitmentPoint, splice);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, splice),
                                                            TestContext.Current.CancellationToken);

        // Assert: our to_remote (its taproot leaf) and the HTLC outputs of the peer's commitment on the splice funding
        Assert.NotNull(outcome);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome.Kind);
        var kinds = _store.Outputs.Values.Select(o => o.Descriptor).ToList();
        Assert.Contains(OutputDescriptorKind.RemoteOfferedHtlc, kinds);
        Assert.Contains(OutputDescriptorKind.RemoteReceivedHtlc, kinds);
        var toRemote = OutputDescriptorData.Decode(_store.Outputs.Values
                                                         .Single(o => o.Descriptor
                                                                   == OutputDescriptorKind.PaymentToRemote)
                                                         .DescriptorData);
        Assert.NotNull(toRemote.TaprootControlBlock);
        Assert.True(toRemote.AmountSat > remote.Spec.LocalMsat / 1_000, "our to_remote must carry the splice-in");
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private ChannelFunding AddPendingSplice(TxId txId, ushort vout)
    {
        // The rotated funding keys of a taproot splice (the P2TR output is not part of a commitment's txid)
        var splice = new ChannelFunding(txId, vout, RealSigningCommitmentPair.FundingSatoshis + 250_000,
                                        TestKeys.Local, TestKeys.Remote, 1, SpliceInMsat, 0, ChannelFundingKind.Splice,
                                        ChannelFundingStatus.Pending, 2_500, 0);
        if (_storedFundings.Count == 0)
            _storedFundings.Add(Current);
        _storedFundings.Add(splice);
        return splice;
    }

    private OutpointSpentEventArgs SpentBy(SignedTransaction spend, ChannelFunding funding) =>
        new(_channel.ChannelId, spend, SpendHeight, 1, funding.FundingTxId, funding.OutputIndex,
            OnchainTestStore.BlockHash(1));

    /// <summary>A taproot commitment of the channel on <paramref name="funding"/>, unsigned.</summary>
    private SignedTransaction BuildCommitment(CommitmentSide side, CommitmentSpec spec, ulong number,
                                              CompactPubKey? remotePoint, ChannelFunding funding)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Local, number)
                        : factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Remote, number,
                                                                   remotePoint);
        Assert.True(model.IsSimpleTaproot);
        model = CommitmentSigningService.WithFunding(model, funding, side);
        return builder.BuildWithOutputMap(model).Transaction;
    }

    private static class TestKeys
    {
        public static readonly CompactPubKey Local =
            Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

        public static readonly CompactPubKey Remote =
            Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");
    }
}