using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Mempool;

using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Application.Onchain.Mempool;
using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Revoked;
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
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Splicing plan §3.6 (SP-I5, NL-479) with BOLT 5 plan O8: a revoked commitment of a funding a splice's lock retired,
/// seen in the mempool (the splice was reorged out), is classified against that funding and gets its penalty stored
/// and published before it confirms, like a breach of the current funding.
/// </summary>
public sealed class MempoolSpliceTests : IDisposable
{
    private const uint Tip = 400;

    private static readonly TxId s_retiredTxId = new(Enumerable.Repeat((byte)0xE1, 32).ToArray());

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly Mock<IChannelFundingDbRepository> _fundings = new();
    private readonly StubRevokedDataSource _dataSource = new();
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private readonly ChannelFunding _retired;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public MempoolSpliceTests()
    {
        // Arrange (shared): an HTLC each way, committed; the channel's funding was replaced by a locked splice, whose
        // predecessor (the retired funding) is still watched
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Add(_pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);
        var current = ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;
        _retired = current with { FundingTxId = s_retiredTxId, Status = ChannelFundingStatus.Replaced };
        _fundings.Setup(f => f.GetByChannelIdAsync(It.IsAny<ChannelId>())).ReturnsAsync([_retired, current]);
        var watch = new WatchedOutpointModel(_retired.FundingTxId, _retired.OutputIndex, _channel.ChannelId,
                                             WatchedOutpointPurpose.FundingOutput);
        _store.Watches[(watch.TransactionId, watch.OutputIndex)] = watch;

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(Tip);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(true);

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(new Mock<IChannelStateDbRepository>().Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundings.Object);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new Domain.Node.Options.NodeOptions()));
        services.AddSingleton(Options.Create(new OnchainOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton(new Mock<IChannelErrorSender>().Object);
        services.AddSingleton(new Mock<IOnchainResolutionExecutor>().Object);
        services.AddSingleton<IOutpointWatcher>(_monitor.Object);
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton(new Mock<Domain.Channels.Interfaces.IHtlcSwitch>().Object);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        services.AddSingleton<IRevokedCommitDataSource>(_dataSource);
        services.AddSingleton<RevokedCommitResolver>();
        services.AddOnchainMempoolServices();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Given_ARevokedCommitmentOfTheRetiredFundingInTheMempool_When_Seen_Then_ItsPenaltyIsPublished()
    {
        // Arrange: Bob's commitment with both HTLCs, revoked, on the retired funding
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildOnRetired(revoked);
        var entry = RevokedCommitmentModel.From(_channel.ChannelId, revoked) with { FundingTxId = s_retiredTxId };
        _dataSource.Context = new RevokedCommitContext(_channel, ChainTxMapper.FromTransaction(
                                                           Transaction.Load(spend.RawTxBytes, Network.RegTest)),
                                                       revoked.Number, secret, revoked.PerCommitmentPoint, entry, 0)
        {
            Funding = _retired
        };
        var args = new MempoolSpendEventArgs(_channel.ChannelId, spend, _retired.FundingTxId, _retired.OutputIndex,
                                             false);

        // Act
        var reaction = await _provider.GetRequiredService<MempoolReactor>()
                                      .HandleSpendAsync(args, TestContext.Current.CancellationToken);

        // Assert: one penalty over every output of the revoked commitment on the retired funding, published
        Assert.Equal(FundingSpendKind.Revoked, reaction.FundingSpendKind);
        var penalty = Assert.Single(_store.Broadcasts);
        Assert.Equal(BroadcastPurpose.Penalty, penalty.Purpose);
        Assert.Equal(penalty.TransactionId, Assert.Single(_published).TransactionId);
        var transaction = Transaction.Load(penalty.RawTransaction, Network.RegTest);
        var commitment = Transaction.Load(spend.RawTxBytes, Network.RegTest);
        Assert.Equal(commitment.Outputs.Count, transaction.Inputs.Count);
        Assert.All(transaction.Inputs, i => Assert.Equal(commitment.GetHash(), i.PrevOut.Hash));
        Assert.Empty(_store.Closes);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>The peer's commitment <paramref name="commit"/> as it was signed on the retired funding.</summary>
    private SignedTransaction BuildOnRetired(RemoteCommit commit)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var model = factory.CreateCommitmentTransactionModel(_channel, CommitmentTxSpec.FromCommitmentSpec(commit.Spec),
                                                             CommitmentSide.Remote, commit.Number,
                                                             commit.PerCommitmentPoint);
        model = CommitmentSigningService.WithFunding(model, _retired, CommitmentSide.Remote);
        return _provider.GetRequiredService<ICommitmentTransactionBuilder>().BuildWithOutputMap(model).Transaction;
    }

    /// <summary>The penalty resolver's reads, served from the test.</summary>
    private sealed class StubRevokedDataSource : IRevokedCommitDataSource
    {
        public RevokedCommitContext? Context { get; set; }

        public Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, CancellationToken cancellationToken) =>
            Task.FromResult(Context is { } context
                                ? RevokedCommitLoadResult.Found(context)
                                : RevokedCommitLoadResult.Missing("no breach"));

        public Task<RevokedOutputSpend?> GetSpendAsync(TxId transactionId, uint outputIndex,
                                                       CancellationToken cancellationToken) =>
            Task.FromResult<RevokedOutputSpend?>(null);

        public Task<bool> IsOurTransactionAsync(TxId transactionId) => Task.FromResult(false);

        public Task<BroadcastTransactionModel?> GetBroadcastAsync(TxId transactionId) =>
            Task.FromResult<BroadcastTransactionModel?>(null);

        public Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey
                                                                                 .ToBytes());

        public Task<uint> GetFeeratePerKwAsync(CancellationToken cancellationToken) => Task.FromResult(2_500u);
    }
}