using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

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
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// NL-877 T4 safety floor: <see cref="OnchainChannelWatcher"/> over <b>real</b> simple taproot commitments
/// (<see cref="RealSigningCommitmentPair"/> with MuSig2, Alice's view): a spend of the P2TR funding output is
/// classified as our commitment, the peer's or a revoked one, without throwing, and the outputs of ours are recorded with
/// the tapscript leaf and control block their script-path spend needs (our <c>to_local</c>, our <c>to_remote</c>, the
/// revoked <c>to_local</c>); HTLC outputs are recorded without one (NL-966).
/// </summary>
public sealed class OnchainChannelWatcherTaprootTests : IDisposable
{
    private const uint SpendHeight = 600;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: true, simpleTaproot: true);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IOnchainResolutionExecutor> _executor = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly ILogger<OnchainChannelWatcher> _logger = NullLogger<OnchainChannelWatcher>.Instance;
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainChannelWatcherTaprootTests()
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

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        // The database's copy of the channel: another instance than the shared in-memory model (NL-307)
        _store.LoadChannel = id => id == _channel.ChannelId ? _pair.Bob.Channel : null;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILogger<OnchainChannelWatcher>>(_logger);
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
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        _provider = services.BuildServiceProvider();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    [Fact]
    public async Task Given_OurTaprootCommitment_When_FundingSpent_Then_LocalCloseWithTheDelayLeafRecorded()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: to_local with its delay leaf and two-leaf control block, both HTLCs and our anchor recorded
        Assert.Equal(ChannelCloseKind.LocalCommitment, outcome!.Kind);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        var kinds = _store.Outputs.Values.Select(o => o.Descriptor).ToList();
        Assert.Contains(OutputDescriptorKind.DelayedToLocal, kinds);
        Assert.Contains(OutputDescriptorKind.LocalOfferedHtlc, kinds);
        Assert.Contains(OutputDescriptorKind.LocalReceivedHtlc, kinds);
        var toLocal = Data(OutputDescriptorKind.DelayedToLocal);
        Assert.Equal(65, toLocal.TaprootControlBlock!.Length);
        Assert.Equal([0x51, 0x20], toLocal.ScriptPubKey[..2]);
        Assert.All(_store.Outputs.Values.Where(o => o.HtlcId is not null),
                   o => Assert.Null(OutputDescriptorData.Decode(o.DescriptorData).TaprootControlBlock));
    }

    [Fact]
    public async Task Given_ThePeersTaprootCommitment_When_FundingSpent_Then_RemoteCloseWithOurToRemoteLeafRecorded()
    {
        // Arrange
        var remote = _pair.Alice.State.RemoteCommit;
        var spend = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome!.Kind);
        var toRemote = Data(OutputDescriptorKind.PaymentToRemote);
        Assert.Equal(33, toRemote.TaprootControlBlock!.Length);
        Assert.Equal(1, toRemote.CsvDelay);
        Assert.Contains(_store.Outputs.Values, o => o.Descriptor == OutputDescriptorKind.RemoteOfferedHtlc);
    }

    [Fact]
    public async Task Given_ARevokedTaprootCommitment_When_FundingSpent_Then_RevokedCloseWithTheRevocationLeafRecorded()
    {
        // Arrange: Bob's commitment with both HTLCs, revoked by the next round
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _store.RevocationLog[(_channel.ChannelId, revoked.Number)] =
            RevokedCommitmentModel.From(_channel.ChannelId, revoked);
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number, revoked.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.Equal(65, Data(OutputDescriptorKind.RevokedToLocal).TaprootControlBlock!.Length);
        Assert.NotNull(Data(OutputDescriptorKind.PaymentToRemote).TaprootControlBlock);
        Assert.Equal(2, _store.Outputs.Values.Count(o => o.Descriptor == OutputDescriptorKind.RevokedHtlc));
    }

    [Fact]
    public async Task Given_AnUnknownSpendOfATaprootFunding_When_Handled_Then_UnknownCloseWithoutThrowing()
    {
        // Arrange: a transaction that spends the funding output and matches no commitment
        var tx = Transaction.Create(Network.Main);
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])_channel.FundingOutput!.TransactionId!.Value),
                                   _channel.FundingOutput.Index!.Value));
        tx.Outputs.Add(Money.Satoshis(900_000), new Key().PubKey.WitHash.ScriptPubKey);
        var spend = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.Unknown, outcome!.Kind);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    private OutputDescriptorData Data(OutputDescriptorKind kind) =>
        OutputDescriptorData.Decode(_store.Outputs.Values.Single(o => o.Descriptor == kind).DescriptorData);

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private OutpointSpentEventArgs SpentBy(SignedTransaction spend) =>
        new(_channel.ChannelId, spend, SpendHeight, 1, _channel.FundingOutput!.TransactionId!.Value,
            _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(1));

    /// <summary>A commitment of the channel as it would be on chain (unsigned: the txid is the same).</summary>
    private SignedTransaction BuildCommitment(CommitmentSide side, CommitmentSpec spec, ulong number,
                                              CompactPubKey? remotePoint)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Local, number)
                        : factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Remote, number,
                                                                   remotePoint);
        return builder.BuildWithOutputMap(model).Transaction;
    }
}