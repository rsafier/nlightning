using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

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
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class FundingSignedMessageHandlerTests
{
    private const ushort FundingOutputIndex = 1;

    private readonly Mock<IBlockchainMonitor> _mockBlockchainMonitor = new();
    private readonly Mock<IFundingTransactionBuilder> _mockFundingTransactionBuilder = new();
    private readonly Mock<ILightningSigner> _mockLightningSigner = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly FundingSignedMessageHandler _handler;
    private readonly ChannelModel _channel;
    private readonly FundingSignedMessage _message;
    private readonly CompactPubKey _peerPubKey;
    private readonly TxId _fundingTxId;

    public FundingSignedMessageHandlerTests()
    {
        var mockChannelMemoryRepository = new Mock<IChannelMemoryRepository>();
        var mockCommitmentTransactionBuilder = new Mock<ICommitmentTransactionBuilder>();
        var mockCommitmentTransactionModelFactory = new Mock<ICommitmentTransactionModelFactory>();
        var mockFundingTransactionModelFactory = new Mock<IFundingTransactionModelFactory>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockUtxoMemoryRepository = new Mock<IUtxoMemoryRepository>();

        CompactPubKey emptyPubKey = new byte[]
        {
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        };
        _peerPubKey = emptyPubKey;
        byte[] channelIdBytes = ChannelId.Zero;
        channelIdBytes[0] = 1;
        ChannelId channelId = channelIdBytes;
        byte[] fundingTxIdBytes = TxId.One;
        fundingTxIdBytes[0] = 0x03;
        _fundingTxId = fundingTxIdBytes;

        var fundingAmount = LightningMoney.Satoshis(10_000);
        var commitmentNumber = new CommitmentNumber(emptyPubKey, emptyPubKey, new FakeSha256());
        var fundingOutputInfo = new FundingOutputInfo(fundingAmount, emptyPubKey, emptyPubKey)
        {
            TransactionId = _fundingTxId,
            Index = FundingOutputIndex
        };
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 3, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                            emptyPubKey);
        _channel = new ChannelModel(channelConfig, channelId, commitmentNumber, fundingOutputInfo, true, null, null,
                                    fundingAmount, keySet, 1, 0, LightningMoney.Zero, keySet, 1, _peerPubKey, 0,
                                    ChannelState.V1FundingCreated, ChannelVersion.V1);

        _message = new FundingSignedMessage(new FundingSignedPayload(channelId, new CompactSignature(new byte[64])));

        mockChannelMemoryRepository
#pragma warning disable CS8601 // Possible null reference assignment.
           .Setup(x => x.TryGetChannel(channelId, out It.Ref<ChannelModel>.IsAny))
#pragma warning restore CS8601 // Possible null reference assignment.
           .Callback((ChannelId _, out ChannelModel? channel) =>
            {
                channel = _channel;
            })
           .Returns(true);

        var commitmentTransactionModel =
            new CommitmentTransactionModel(commitmentNumber, LightningMoney.Zero, fundingOutputInfo);
        mockCommitmentTransactionModelFactory
           .Setup(x => x.CreateCommitmentTransactionModel(It.IsAny<ChannelModel>(), CommitmentSide.Local))
           .Returns(commitmentTransactionModel);
        mockCommitmentTransactionBuilder
           .Setup(x => x.Build(commitmentTransactionModel))
           .Returns(new SignedTransaction(TxId.Zero, [0x00, 0x01]));

        var utxos = new List<UtxoModel>
        {
            new(TxId.One, 0, LightningMoney.Satoshis(20_000), 100, 0, false, AddressType.P2Wpkh)
        };
        mockUtxoMemoryRepository.Setup(x => x.GetLockedUtxosForChannel(channelId)).Returns(utxos);
        mockFundingTransactionModelFactory
           .Setup(x => x.Create(It.IsAny<ChannelModel>(), utxos, It.IsAny<WalletAddressModel?>()))
           .Returns(new FundingTransactionModel(utxos, fundingOutputInfo, LightningMoney.Satoshis(1_000)));

        _mockLightningSigner
           .Setup(x => x.SignFundingTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()))
           .Returns(true);
        mockUnitOfWork.Setup(x => x.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _mockChannelDbRepository
           .Setup(x => x.GetByIdAsync(It.IsAny<ChannelId>()))
           .ReturnsAsync((ChannelModel?)null);

        _handler = new FundingSignedMessageHandler(_mockBlockchainMonitor.Object, mockChannelMemoryRepository.Object,
                                                   mockCommitmentTransactionBuilder.Object,
                                                   mockCommitmentTransactionModelFactory.Object,
                                                   _mockFundingTransactionBuilder.Object,
                                                   mockFundingTransactionModelFactory.Object,
                                                   _mockLightningSigner.Object,
                                                   new Mock<ILogger<FundingSignedMessageHandler>>().Object,
                                                   mockUnitOfWork.Object, mockUtxoMemoryRepository.Object);
    }

    [Fact]
    public async Task Given_RebuiltTxMatchesFundingOutpoint_When_Handling_Then_SignsAndPublishes()
    {
        // Arrange
        var fundingTransaction = new SignedTransaction(_fundingTxId, [0x02, 0x00]);
        _mockFundingTransactionBuilder
           .Setup(x => x.Build(It.IsAny<FundingTransactionModel>()))
           .Returns(new FundingTransactionBuildResult(fundingTransaction, FundingOutputIndex));

        // Act
        var result = await _handler.HandleAsync(_message, ChannelState.V1FundingCreated, new FeatureOptions(),
                                                _peerPubKey);

        // Assert
        Assert.Empty(result);
        _mockLightningSigner.Verify(x => x.SignFundingTransaction(_channel.ChannelId, fundingTransaction),
                                    Times.Once);
        _mockBlockchainMonitor.Verify(
            x => x.PublishAndWatchTransactionAsync(_channel.ChannelId, fundingTransaction, It.IsAny<uint>()),
            Times.Once);
        Assert.Equal(ChannelState.V1FundingSigned, _channel.State);
    }

    [Fact]
    public async Task Given_RebuiltTxHasDifferentTxId_When_Handling_Then_ThrowsWithoutSigningOrPublishing()
    {
        // Arrange
        var otherTxIdBytes = ((byte[])_fundingTxId).ToArray();
        otherTxIdBytes[1] = 0xaa;
        _mockFundingTransactionBuilder
           .Setup(x => x.Build(It.IsAny<FundingTransactionModel>()))
           .Returns(new FundingTransactionBuildResult(new SignedTransaction(otherTxIdBytes, [0x02, 0x00]),
                                                      FundingOutputIndex));

        // Act / Assert
        await AssertMismatchIsRejectedAsync();
    }

    [Fact]
    public async Task Given_RebuiltTxHasDifferentFundingIndex_When_Handling_Then_ThrowsWithoutSigningOrPublishing()
    {
        // Arrange
        _mockFundingTransactionBuilder
           .Setup(x => x.Build(It.IsAny<FundingTransactionModel>()))
           .Returns(new FundingTransactionBuildResult(new SignedTransaction(_fundingTxId, [0x02, 0x00]), 0));

        // Act / Assert
        await AssertMismatchIsRejectedAsync();
    }

    private async Task AssertMismatchIsRejectedAsync()
    {
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => _handler.HandleAsync(_message, ChannelState.V1FundingCreated,
                                                       new FeatureOptions(), _peerPubKey));

        Assert.Equal("Rebuilt funding transaction does not match the channel funding outpoint", exception.Message);
        _mockLightningSigner.Verify(
            x => x.SignFundingTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()), Times.Never);
        _mockBlockchainMonitor.Verify(
            x => x.PublishAndWatchTransactionAsync(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>(),
                                                   It.IsAny<uint>()), Times.Never);
        _mockChannelDbRepository.Verify(x => x.AddAsync(It.IsAny<ChannelModel>()), Times.Never);
        Assert.Equal(ChannelState.V1FundingCreated, _channel.State);
    }
}