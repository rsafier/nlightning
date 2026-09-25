using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Validators.Parameters;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class AcceptChannel1MessageHandlerTests
{
    private static readonly CompactPubKey s_pubKey = CreatePubKey(0x01);
    private static readonly ChannelId s_tempChannelId = CreateChannelId(0x01);
    private static readonly ChannelId s_newChannelId = CreateChannelId(0x02);

    private readonly Mock<IChannelMemoryRepository> _mockChannelMemoryRepository = new();
    private readonly Mock<IChannelDbRepository> _mockChannelDbRepository = new();
    private readonly Mock<ICommitmentTransactionBuilder> _mockCommitmentTransactionBuilder = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly Mock<IUtxoMemoryRepository> _mockUtxoMemoryRepository = new();
    private readonly ChannelModel _tempChannel;
    private readonly AcceptChannel1MessageHandler _handler;

    public AcceptChannel1MessageHandlerTests()
    {
        _tempChannel = CreateTempChannel();

        _mockChannelMemoryRepository
           .Setup(r => r.TryGetTemporaryChannelState(s_pubKey, s_tempChannelId, out It.Ref<ChannelState>.IsAny))
           .Callback((CompactPubKey _, ChannelId _, out ChannelState state) =>
            {
                state = ChannelState.V1Opening;
            })
           .Returns(true);
        _mockChannelMemoryRepository
           .Setup(r => r.TryGetTemporaryChannel(s_pubKey, s_tempChannelId, out It.Ref<ChannelModel>.IsAny!))
           .Callback((CompactPubKey _, ChannelId _, out ChannelModel channel) =>
            {
                channel = _tempChannel;
            })
           .Returns(true);
        _mockChannelMemoryRepository.Setup(r => r.TryRemoveTemporaryChannel(s_pubKey, s_tempChannelId))
                                    .Returns(true);

        _mockUnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_mockChannelDbRepository.Object);
        _mockChannelDbRepository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>())).ReturnsAsync((ChannelModel?)null);

        _mockUtxoMemoryRepository.Setup(r => r.GetLockedUtxosForChannel(It.IsAny<ChannelId>())).Returns([]);

        var mockWalletService = new Mock<IBitcoinWalletService>();
        mockWalletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                         .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, true, "bcrt1qchange"));

        var mockFundingTransactionModelFactory = new Mock<IFundingTransactionModelFactory>();
        mockFundingTransactionModelFactory
           .Setup(f => f.Create(It.IsAny<ChannelModel>(), It.IsAny<List<UtxoModel>>(),
                                It.IsAny<WalletAddressModel?>()))
           .Returns((ChannelModel c, List<UtxoModel> u, WalletAddressModel? _) =>
                        new FundingTransactionModel(u, c.FundingOutput!, LightningMoney.Zero));

        var mockFundingTransactionBuilder = new Mock<IFundingTransactionBuilder>();
        mockFundingTransactionBuilder
           .Setup(b => b.Build(It.IsAny<FundingTransactionModel>()))
           .Callback((FundingTransactionModel m) =>
            {
                m.FundingOutput.TransactionId = TxId.One;
                m.FundingOutput.Index = 0;
            })
           .Returns(new SignedTransaction(TxId.One, [0x00]));

        var mockChannelIdFactory = new Mock<IChannelIdFactory>();
        mockChannelIdFactory.Setup(f => f.CreateV1(It.IsAny<TxId>(), It.IsAny<ushort>())).Returns(s_newChannelId);

        var mockCommitmentTransactionModelFactory = new Mock<ICommitmentTransactionModelFactory>();
        mockCommitmentTransactionModelFactory
           .Setup(f => f.CreateCommitmentTransactionModel(It.IsAny<ChannelModel>(), CommitmentSide.Remote))
           .Returns((ChannelModel c, CommitmentSide _) =>
                        new CommitmentTransactionModel(c.CommitmentNumber!, LightningMoney.Zero, c.FundingOutput!));
        _mockCommitmentTransactionBuilder.Setup(b => b.Build(It.IsAny<CommitmentTransactionModel>()))
                                         .Returns(new SignedTransaction(TxId.Zero, [0x01]));

        var signature = new CompactSignature(new byte[64]);
        var mockSigner = new Mock<ILightningSigner>();
        mockSigner.Setup(s => s.SignChannelTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()))
                  .Returns(signature);

        var mockMessageFactory = new Mock<IMessageFactory>();
        mockMessageFactory
           .Setup(f => f.CreateFundingCreatedMessage(It.IsAny<ChannelId>(), It.IsAny<TxId>(), It.IsAny<ushort>(),
                                                     It.IsAny<CompactSignature>()))
           .Returns((ChannelId id, TxId txId, ushort index, CompactSignature sig) =>
                        new FundingCreatedMessage(new FundingCreatedPayload(id, txId, index, sig)));

        var mockValidator = new Mock<IChannelOpenValidator>();
        uint minimumDepth = 3;
        mockValidator.Setup(v => v.PerformMandatoryChecks(It.IsAny<ChannelOpenMandatoryValidationParameters>(),
                                                          out minimumDepth));

        _handler = new AcceptChannel1MessageHandler(mockWalletService.Object, mockChannelIdFactory.Object,
                                                    _mockChannelMemoryRepository.Object, mockValidator.Object,
                                                    _mockCommitmentTransactionBuilder.Object,
                                                    mockCommitmentTransactionModelFactory.Object,
                                                    mockFundingTransactionBuilder.Object,
                                                    mockFundingTransactionModelFactory.Object, mockSigner.Object,
                                                    new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                    mockMessageFactory.Object, new FakeSha256(),
                                                    _mockUnitOfWork.Object, _mockUtxoMemoryRepository.Object);
    }

    [Fact]
    public async Task Given_NoUpfrontShutdownScriptAndFeatureNotNegotiated_When_HandleAsync_Then_ChannelIsAccepted()
    {
        // Arrange
        var message = CreateMessage(null);
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = FeatureSupport.No };

        // Act
        var result = await _handler.HandleAsync(message, ChannelState.None, negotiatedFeatures, s_pubKey);

        // Assert
        Assert.IsType<FundingCreatedMessage>(result);
    }

    [Fact]
    public async Task Given_NoUpfrontShutdownScriptAndFeatureNegotiated_When_HandleAsync_Then_ChannelIsRejected()
    {
        // Arrange
        var message = CreateMessage(null);
        var negotiatedFeatures = new FeatureOptions { UpfrontShutdownScript = FeatureSupport.Optional };

        // Act & Assert
        await Assert.ThrowsAsync<ChannelErrorException>(() => _handler.HandleAsync(
                                                            message, ChannelState.None, negotiatedFeatures,
                                                            s_pubKey));
    }

    [Fact]
    public async Task Given_ValidAcceptChannel_When_HandleAsync_Then_ChannelIsNotPersistedBeforeFundingSigned()
    {
        // Arrange
        var message = CreateMessage(new UpfrontShutdownScriptTlv(Array.Empty<byte>()));

        // Act
        var result = await _handler.HandleAsync(message, ChannelState.None, new FeatureOptions(), s_pubKey);

        // Assert
        // BOLT 2: a funder that has not broadcast the funding tx SHOULD NOT remember the channel on disconnection
        Assert.IsType<FundingCreatedMessage>(result);
        _mockChannelDbRepository.Verify(r => r.AddAsync(It.IsAny<ChannelModel>()), Times.Never);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Never);
        _mockChannelMemoryRepository.Verify(r => r.UpgradeChannel(s_tempChannelId, _tempChannel), Times.Once);
        _mockUtxoMemoryRepository.Verify(r => r.UpgradeChannelIdOnLockedUtxos(s_tempChannelId, s_newChannelId),
                                         Times.Once);
    }

    [Fact]
    public async Task Given_ErrorAfterChannelIdChanged_When_HandleAsync_Then_TempChannelIsRemovedAndUtxosReleased()
    {
        // Arrange
        var message = CreateMessage(new UpfrontShutdownScriptTlv(Array.Empty<byte>()));
        _mockCommitmentTransactionBuilder.Setup(b => b.Build(It.IsAny<CommitmentTransactionModel>()))
                                         .Throws(new InvalidOperationException("boom"));

        // Act
        await Assert.ThrowsAsync<ChannelErrorException>(() => _handler.HandleAsync(
                                                            message, ChannelState.None, new FeatureOptions(),
                                                            s_pubKey));

        // Assert
        _mockChannelMemoryRepository.Verify(r => r.TryRemoveTemporaryChannel(s_pubKey, s_tempChannelId), Times.Once);
        _mockUtxoMemoryRepository.Verify(r => r.ReturnUtxosNotSpentOnChannel(s_tempChannelId), Times.Once);
        _mockChannelMemoryRepository.Verify(r => r.UpgradeChannel(It.IsAny<ChannelId>(), It.IsAny<ChannelModel>()),
                                            Times.Never);
        _mockChannelDbRepository.Verify(r => r.AddAsync(It.IsAny<ChannelModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_ErrorBeforeChannelIdChanged_When_HandleAsync_Then_TempChannelIsRemovedAndUtxosReleased()
    {
        // Arrange
        var message = CreateMessage(new UpfrontShutdownScriptTlv(Array.Empty<byte>()));
        _mockUtxoMemoryRepository.Setup(r => r.GetLockedUtxosForChannel(It.IsAny<ChannelId>()))
                                 .Throws(new InvalidOperationException("boom"));

        // Act
        await Assert.ThrowsAsync<ChannelErrorException>(() => _handler.HandleAsync(
                                                            message, ChannelState.None, new FeatureOptions(),
                                                            s_pubKey));

        // Assert
        _mockChannelMemoryRepository.Verify(r => r.TryRemoveTemporaryChannel(s_pubKey, s_tempChannelId), Times.Once);
        _mockUtxoMemoryRepository.Verify(r => r.ReturnUtxosNotSpentOnChannel(s_tempChannelId), Times.Once);
    }

    private static AcceptChannel1Message CreateMessage(UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv)
    {
        var payload = new AcceptChannel1Payload(s_tempChannelId, LightningMoney.Satoshis(1_000), s_pubKey,
                                                LightningMoney.Satoshis(546), s_pubKey, s_pubKey, s_pubKey,
                                                LightningMoney.Zero, 30, LightningMoney.Satoshis(100_000), 3,
                                                s_pubKey, s_pubKey, 144);
        return new AcceptChannel1Message(payload, new ChannelTypeTlv([0x10, 0x00]), upfrontShutdownScriptTlv);
    }

    private static ChannelModel CreateTempChannel()
    {
        var channelConfig = new ChannelConfig(LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(253),
                                              LightningMoney.Zero, LightningMoney.Satoshis(546), 30,
                                              LightningMoney.Satoshis(100_000), 3, false, LightningMoney.Zero, 144,
                                              FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);

        return new ChannelModel(channelConfig, s_tempChannelId, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0, s_pubKey,
                                0, ChannelState.V1Opening, ChannelVersion.V1);
    }

    private static CompactPubKey CreatePubKey(byte last)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = last;
        return bytes;
    }

    private static ChannelId CreateChannelId(byte first)
    {
        var bytes = new byte[32];
        bytes[0] = first;
        return bytes;
    }
}