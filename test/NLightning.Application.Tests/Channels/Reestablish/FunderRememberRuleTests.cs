using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Reestablish;

using Application.Channels.Handlers;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Handlers;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT2 plan N7-T6 / B2-RE-01 (NL-048, wontfix): BOLT 2 "Message Retransmission" says a funder that has not
/// broadcast the funding transaction SHOULD NOT remember the channel on disconnect. At funding_signed the funder saves,
/// in one save, the channel as V1FundingSigned, the funding watch, the signed funding transaction (BOLT 5 plan O0-T1)
/// and the watch of the funding output, and only then publishes: from that save on the transaction is certain to go
/// out (the chain monitor sends every stored pending transaction again after each block and at startup, NL-258), so
/// remembering the channel is right. Before funding_signed nothing is persisted. These tests pin that order.
/// </summary>
/// <remarks>
/// <see cref="IBlockchainMonitor"/> is mocked here; the rebroadcast itself is proven on SQLite by
/// <c>Integration.Tests/Persistence/ChainMonitorPersistenceTests</c>.
/// </remarks>
public class FunderRememberRuleTests
{
    private const ushort FundingOutputIndex = 1;

    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<string> _steps = [];
    private readonly FundingSignedMessageHandler _handler;
    private readonly ChannelModel _channel;
    private readonly FundingSignedMessage _message;
    private readonly CompactPubKey _peer = NormalOperationTestContext.PeerNodeId;

    public FunderRememberRuleTests()
    {
        var memory = new Mock<IChannelMemoryRepository>();
        var commitmentBuilder = new Mock<ICommitmentTransactionBuilder>();
        var commitmentModelFactory = new Mock<ICommitmentTransactionModelFactory>();
        var fundingModelFactory = new Mock<IFundingTransactionModelFactory>();
        var fundingBuilder = new Mock<IFundingTransactionBuilder>();
        var utxoMemory = new Mock<IUtxoMemoryRepository>();

        var key = NormalOperationTestContext.Point(0x01);
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x21, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x22, 32).ToArray());
        var fundingAmount = LightningMoney.Satoshis(10_000);
        var commitmentNumber = new CommitmentNumber(key, key, new FakeSha256());
        var fundingOutput = new FundingOutputInfo(fundingAmount, key, key)
        {
            TransactionId = fundingTxId,
            Index = FundingOutputIndex
        };
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                                     LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, key, key, key, key, key, key);
        _channel = new ChannelModel(channelParams, channelId, commitmentNumber, fundingOutput, true, null, null,
                                    fundingAmount, keySet, 1, 0, LightningMoney.Zero, keySet, 1, _peer, 0,
                                    ChannelState.V1FundingCreated, ChannelVersion.V1);
        _message = new FundingSignedMessage(new FundingSignedPayload(channelId, new CompactSignature(new byte[64])));

        memory.Setup(m => m.TryGetChannel(channelId, out It.Ref<ChannelModel>.IsAny!))
              .Returns(new TryGetChannelDelegate((ChannelId _, out ChannelModel channel) =>
               {
                   channel = _channel;
                   return true;
               }));

        var commitmentModel = new CommitmentTransactionModel(commitmentNumber, 0, LightningMoney.Zero, fundingOutput);
        commitmentModelFactory
           .Setup(f => f.CreateCommitmentTransactionModel(It.IsAny<ChannelModel>(), CommitmentSide.Local, 0UL))
           .Returns(commitmentModel);
        commitmentBuilder.Setup(b => b.Build(commitmentModel)).Returns(new SignedTransaction(TxId.Zero, [0x00]));

        var utxos = new List<UtxoModel>
        {
            new(TxId.One, 0, LightningMoney.Satoshis(20_000), 100, 0, false, AddressType.P2Wpkh)
        };
        utxoMemory.Setup(u => u.GetLockedUtxosForChannel(channelId)).Returns(utxos);
        fundingModelFactory.Setup(f => f.Create(It.IsAny<ChannelModel>(), utxos, It.IsAny<WalletAddressModel?>()))
                           .Returns(new FundingTransactionModel(utxos, fundingOutput, LightningMoney.Satoshis(1_000)));
        fundingBuilder.Setup(b => b.Build(It.IsAny<FundingTransactionModel>()))
                      .Returns(new FundingTransactionBuildResult(new SignedTransaction(fundingTxId, [0x02]),
                                                                 FundingOutputIndex));

        _signer.Setup(s => s.SignFundingTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()))
               .Returns(true);
        _unitOfWork.Setup(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        var stored = false;
        _channelDb.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync(() => stored ? _channel : null);
        _channelDb.Setup(r => r.AddAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) =>
                   {
                       stored = true;
                       _steps.Add($"add {c.State}");
                   })
                  .Returns(Task.CompletedTask);
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) => _steps.Add($"update {c.State}"))
                  .Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _steps.Add("save")).Returns(Task.CompletedTask);
        var watchedTransactions = new Mock<IWatchedTransactionDbRepository>();
        watchedTransactions.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                           .Callback((WatchedTransactionModel w) => _steps.Add($"stage watch {w.RequiredDepth}"));
        _unitOfWork.Setup(u => u.WatchedTransactionDbRepository).Returns(watchedTransactions.Object);
        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>()))
                  .Callback((BroadcastTransactionModel b) =>
                   {
                       StagedBroadcast = b;
                       _steps.Add($"stage {b.Purpose}");
                   });
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        var outpoints = new Mock<IWatchedOutpointDbRepository>();
        outpoints.Setup(r => r.Add(It.IsAny<WatchedOutpointModel>()))
                 .Callback((WatchedOutpointModel o) => _steps.Add($"stage outpoint {o.OutputIndex} {o.Purpose}"));
        _unitOfWork.Setup(u => u.WatchedOutpointDbRepository).Returns(outpoints.Object);
        _blockchainMonitor.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                          .Callback(() => _steps.Add("publish"))
                          .ReturnsAsync(true);

        _handler = new FundingSignedMessageHandler(_blockchainMonitor.Object, memory.Object, commitmentBuilder.Object,
                                                   commitmentModelFactory.Object, fundingBuilder.Object,
                                                   fundingModelFactory.Object, _signer.Object,
                                                   NullLogger<FundingSignedMessageHandler>.Instance,
                                                   _unitOfWork.Object, utxoMemory.Object);
    }

    private BroadcastTransactionModel? StagedBroadcast { get; set; }

    private delegate bool TryGetChannelDelegate(ChannelId channelId, out ChannelModel channel);

    [Fact]
    public async Task Given_FundingSigned_Then_PersistedBeforeBroadcast()
    {
        // Act
        await _handler.HandleAsync(_message, ChannelState.V1FundingCreated, new FeatureOptions(), _peer);

        // Assert - one save holds the channel as V1FundingSigned, the funding watch, the funding transaction and the
        // funding output watch; the publish comes after it
        Assert.Equal(["add V1FundingSigned", "stage watch 3", "stage Funding", "stage outpoint 1 FundingOutput", "save",
                      "publish"], _steps);
        Assert.Equal(ChannelState.V1FundingSigned, _channel.State);
        Assert.NotNull(StagedBroadcast);
        Assert.Equal(_channel.ChannelId, StagedBroadcast.ChannelId);
        Assert.Equal(new byte[] { 0x02 }, StagedBroadcast.RawTransaction);
        Assert.Equal(BroadcastState.Pending, StagedBroadcast.State);
        _blockchainMonitor.Verify(b => b.TrackWatchedTransaction(It.IsAny<WatchedTransactionModel>()), Times.Once);
        _blockchainMonitor.Verify(b => b.TrackWatchedOutpoint(It.Is<WatchedOutpointModel>(o => o.OutputIndex == 1)),
                                  Times.Once);
    }

    [Fact]
    public async Task Given_FundingSignedWithABadSignature_When_Handled_Then_NothingIsRememberedOrBroadcast()
    {
        // Arrange
        _signer.Setup(s => s.ValidateSignature(It.IsAny<ChannelId>(), It.IsAny<CompactSignature>(),
                                               It.IsAny<SignedTransaction>()))
               .Throws(new ChannelErrorException("bad signature"));

        // Act
        await Assert.ThrowsAsync<ChannelErrorException>(
            () => _handler.HandleAsync(_message, ChannelState.V1FundingCreated, new FeatureOptions(), _peer));

        // Assert
        Assert.Empty(_steps);
        Assert.Equal(ChannelState.V1FundingCreated, _channel.State);
    }

    [Fact]
    public async Task Given_TheBroadcastIsRefused_When_Handled_Then_TheChannelIsStoredWithItsTransactionForRebroadcast()
    {
        // Arrange (NL-258: before O0 a refused publish left a watch for a transaction nothing sent again)
        _blockchainMonitor.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                          .Callback(() => _steps.Add("publish"))
                          .ReturnsAsync(false);

        // Act
        var replies = await _handler.HandleAsync(_message, ChannelState.V1FundingCreated, new FeatureOptions(), _peer);

        // Assert - nothing thrown: the stored pending transaction is sent again after every block
        Assert.Empty(replies);
        Assert.Equal(["add V1FundingSigned", "stage watch 3", "stage Funding", "stage outpoint 1 FundingOutput", "save",
                      "publish"], _steps);
        Assert.Equal(ChannelState.V1FundingSigned, _channel.State);
    }
}