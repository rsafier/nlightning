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
    private const ushort BuiltFundingOutputIndex = 1;
    private const uint MinimumDepth = 3;

    private readonly Mock<IChannelIdFactory> _mockChannelIdFactory = new();
    private readonly Mock<IMessageFactory> _mockMessageFactory = new();
    private readonly AcceptChannel1MessageHandler _handler;
    private readonly ChannelModel _tempChannel;
    private readonly AcceptChannel1Message _message;
    private readonly CompactPubKey _peerPubKey;
    private readonly ChannelId _tempChannelId;
    private readonly ChannelId _newChannelId;
    private readonly TxId _builtTxId;
    private readonly CompactSignature _ourSignature;

    public AcceptChannel1MessageHandlerTests()
    {
        var mockBitcoinWalletService = new Mock<IBitcoinWalletService>();
        var mockChannelMemoryRepository = new Mock<IChannelMemoryRepository>();
        var mockChannelOpenValidator = new Mock<IChannelOpenValidator>();
        var mockCommitmentTransactionBuilder = new Mock<ICommitmentTransactionBuilder>();
        var mockCommitmentTransactionModelFactory = new Mock<ICommitmentTransactionModelFactory>();
        var mockFundingTransactionBuilder = new Mock<IFundingTransactionBuilder>();
        var mockFundingTransactionModelFactory = new Mock<IFundingTransactionModelFactory>();
        var mockLightningSigner = new Mock<ILightningSigner>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockChannelDbRepository = new Mock<IChannelDbRepository>();
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
        _tempChannelId = ChannelId.Zero;
        byte[] newChannelIdBytes = ChannelId.Zero;
        newChannelIdBytes[0] = 1;
        _newChannelId = newChannelIdBytes;
        byte[] builtTxIdBytes = TxId.One;
        builtTxIdBytes[0] = 0x03;
        _builtTxId = builtTxIdBytes;
        var ourSignatureBytes = new byte[64];
        ourSignatureBytes[0] = 1;
        _ourSignature = new CompactSignature(ourSignatureBytes);

        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, MinimumDepth, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey, emptyPubKey,
                                                 emptyPubKey);
        _tempChannel = new ChannelModel(channelConfig, _tempChannelId, null, null, true, null, null,
                                        LightningMoney.Satoshis(10_000), localKeySet, 1, 0, LightningMoney.Zero,
                                        null, 1, _peerPubKey, 0, ChannelState.V1Opening, ChannelVersion.V1);

        var payload = new AcceptChannel1Payload(_tempChannelId, LightningMoney.Zero, emptyPubKey, LightningMoney.Zero,
                                                emptyPubKey, emptyPubKey, emptyPubKey, LightningMoney.Zero, 30,
                                                LightningMoney.Zero, MinimumDepth, emptyPubKey, emptyPubKey, 144);
        _message = new AcceptChannel1Message(payload, new ChannelTypeTlv([]),
                                             new UpfrontShutdownScriptTlv(new byte[] { }));

        mockChannelMemoryRepository
           .Setup(x => x.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                     out It.Ref<ChannelState>.IsAny))
           .Returns(false);
        mockChannelMemoryRepository
#pragma warning disable CS8601 // Possible null reference assignment.
           .Setup(x => x.TryGetTemporaryChannel(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                out It.Ref<ChannelModel>.IsAny))
#pragma warning restore CS8601 // Possible null reference assignment.
           .Callback((CompactPubKey _, ChannelId _, out ChannelModel? channel) =>
            {
                channel = _tempChannel;
            })
           .Returns(true);

        var minimumDepth = MinimumDepth;
        mockChannelOpenValidator
           .Setup(x => x.PerformMandatoryChecks(It.IsAny<ChannelOpenMandatoryValidationParameters>(),
                                                out minimumDepth));

        var utxos = new List<UtxoModel>
        {
            new(TxId.One, 0, LightningMoney.Satoshis(20_000), 100, 0, false, AddressType.P2Wpkh)
        };
        mockUtxoMemoryRepository.Setup(x => x.GetLockedUtxosForChannel(It.IsAny<ChannelId>())).Returns(utxos);
        mockBitcoinWalletService
           .Setup(x => x.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
           .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, true, "address"));
        mockFundingTransactionModelFactory
           .Setup(x => x.Create(It.IsAny<ChannelModel>(), utxos, It.IsAny<WalletAddressModel?>()))
           .Returns((ChannelModel channel, List<UtxoModel> u, WalletAddressModel? _) =>
                        new FundingTransactionModel(u, channel.FundingOutput!, LightningMoney.Satoshis(1_000)));
        mockFundingTransactionBuilder
           .Setup(x => x.Build(It.IsAny<FundingTransactionModel>()))
           .Returns(new FundingTransactionBuildResult(new SignedTransaction(_builtTxId, [0x02, 0x00]),
                                                      BuiltFundingOutputIndex));

        _mockChannelIdFactory.Setup(x => x.CreateV1(It.IsAny<TxId>(), It.IsAny<ushort>())).Returns(_newChannelId);
        mockUnitOfWork.Setup(x => x.ChannelDbRepository).Returns(mockChannelDbRepository.Object);
        mockChannelDbRepository
           .Setup(x => x.GetByIdAsync(It.IsAny<ChannelId>()))
           .ReturnsAsync((ChannelModel?)null);

        mockCommitmentTransactionModelFactory
           .Setup(x => x.CreateCommitmentTransactionModel(It.IsAny<ChannelModel>(), CommitmentSide.Remote))
           .Returns((ChannelModel channel, CommitmentSide _) =>
                        new CommitmentTransactionModel(channel.CommitmentNumber!, LightningMoney.Zero,
                                                       channel.FundingOutput!));
        mockCommitmentTransactionBuilder
           .Setup(x => x.Build(It.IsAny<CommitmentTransactionModel>()))
           .Returns(new SignedTransaction(TxId.Zero, [0x00, 0x01]));
        mockLightningSigner
           .Setup(x => x.SignChannelTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()))
           .Returns(_ourSignature);

        _mockMessageFactory
           .Setup(x => x.CreateFundingCreatedMessage(It.IsAny<ChannelId>(), It.IsAny<TxId>(), It.IsAny<ushort>(),
                                                     It.IsAny<CompactSignature>()))
           .Returns((ChannelId channelId, TxId txId, ushort index, CompactSignature signature) =>
                        new FundingCreatedMessage(new FundingCreatedPayload(channelId, txId, index, signature)));

        _handler = new AcceptChannel1MessageHandler(mockBitcoinWalletService.Object, _mockChannelIdFactory.Object,
                                                    mockChannelMemoryRepository.Object,
                                                    mockChannelOpenValidator.Object,
                                                    mockCommitmentTransactionBuilder.Object,
                                                    mockCommitmentTransactionModelFactory.Object,
                                                    mockFundingTransactionBuilder.Object,
                                                    mockFundingTransactionModelFactory.Object,
                                                    mockLightningSigner.Object,
                                                    new Mock<ILogger<OpenChannel1MessageHandler>>().Object,
                                                    _mockMessageFactory.Object, new FakeSha256(),
                                                    mockUnitOfWork.Object, mockUtxoMemoryRepository.Object);
    }

    [Fact]
    public async Task Given_BuilderReturnsNonZeroIndex_When_Handling_Then_FundingOutpointFlowsIntoFundingCreated()
    {
        // Act
        var result = await _handler.HandleAsync(_message, ChannelState.None, new FeatureOptions(), _peerPubKey);

        // Assert
        var fundingCreated = Assert.IsType<FundingCreatedMessage>(result);
        Assert.Equal(_builtTxId, _tempChannel.FundingOutput?.TransactionId);
        Assert.Equal(BuiltFundingOutputIndex, _tempChannel.FundingOutput?.Index);
        Assert.Equal(_builtTxId, fundingCreated.Payload.FundingTxId);
        Assert.Equal(BuiltFundingOutputIndex, fundingCreated.Payload.FundingOutputIndex);
        Assert.Equal(_tempChannelId, fundingCreated.Payload.ChannelId);
        _mockChannelIdFactory.Verify(x => x.CreateV1(_builtTxId, BuiltFundingOutputIndex), Times.Once);
        Assert.Equal(_newChannelId, _tempChannel.ChannelId);
    }
}