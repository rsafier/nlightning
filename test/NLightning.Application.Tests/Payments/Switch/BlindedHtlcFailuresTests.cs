using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Onion;
using Application.Payments.Switch;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// M5 review (lane rf1-m5): the BOLT 2 blinded failure rules shared by the paths that fail incoming HTLCs outside the
/// switch (dust exposure, HTLC expiry), the introduction role read from the onion whatever the feature setting, and the
/// choice of the outgoing channel of a blinded forward by <c>next_node_id</c>.
/// </summary>
public class BlindedHtlcFailuresTests
{
    private static readonly Secret s_sharedSecret = new(Enumerable.Repeat((byte)0x5E, 32).ToArray());
    private static readonly CompactPubKey s_pathKey = Point(0x33);

    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IFailureOnionService> _failureOnion = new();
    private readonly List<FailureMessage> _failures = [];

    public BlindedHtlcFailuresTests()
    {
        _failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                     .Callback((Secret _, FailureMessage message, int _) => _failures.Add(message))
                     .Returns(new byte[292]);
    }

    [Fact]
    public async Task Given_HtlcWithPathKey_When_Failed_Then_MalformedInvalidOnionBlindingOnly()
    {
        // Act
        var sent = await BlindedHtlcFailures.FailAsync(_operations.Object, _failureOnion.Object, TestChannelId,
                                                       Htlc(s_pathKey), s_sharedSecret,
                                                       FailureMessage.TemporaryNodeFailure(), false,
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("invalid_onion_blinding", sent);
        _operations.Verify(o => o.FailMalformedHtlcAsync(TestChannelId, 7, FailureCode.InvalidOnionBlinding,
                                                         It.IsAny<Hash>(), It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_IntroductionForward_When_Failed_Then_OurOwnInvalidOnionBlindingInAnErrorOnion()
    {
        // Act
        await BlindedHtlcFailures.FailAsync(_operations.Object, _failureOnion.Object, TestChannelId, Htlc(null),
                                            s_sharedSecret, FailureMessage.TemporaryNodeFailure(), true,
                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Assert.Single(_failures).Code);
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, 7, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_HtlcOutsideABlindedRoute_When_Failed_Then_TheGivenFailureIsSent()
    {
        // Act
        await BlindedHtlcFailures.FailAsync(_operations.Object, _failureOnion.Object, TestChannelId, Htlc(null),
                                            s_sharedSecret, FailureMessage.TemporaryNodeFailure(), false,
                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.TemporaryNodeFailure, Assert.Single(_failures).Code);
    }

    [Theory]
    [InlineData(FeatureSupport.Optional)]
    [InlineData(FeatureSupport.No)]
    public async Task Given_IntroductionPayload_When_RoleChecked_Then_IntroductionWhateverTheFeatureSetting(
        FeatureSupport routeBlinding)
    {
        // Arrange - a restart with option_route_blinding off must not turn a blinded forward into a plain one
        var processor = Processor(new HopPayload(new EncryptedRecipientDataTlv(new byte[] { 1, 2, 3 }),
                                                 new CurrentPathKeyTlv(s_pathKey)), routeBlinding);

        // Act
        var isIntroduction = await BlindedHtlcFailures.IsIntroductionForwardAsync(processor, Htlc(null));

        // Assert
        Assert.True(isIntroduction);
    }

    [Fact]
    public async Task Given_PlainForwardPayload_When_RoleChecked_Then_NotIntroduction()
    {
        // Arrange
        var processor = Processor(new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                                 new OutgoingCltvValueTlv(500),
                                                 new OnionShortChannelIdTlv(new ShortChannelId(1, 2, 3))),
                                  FeatureSupport.Optional);

        // Act / Assert
        Assert.False(await BlindedHtlcFailures.IsIntroductionForwardAsync(processor, Htlc(null)));
    }

    [Fact]
    public async Task Given_HtlcWithPathKey_When_RoleChecked_Then_NotIntroduction()
    {
        // Arrange
        var processor = Processor(new HopPayload(), FeatureSupport.Optional);

        // Act / Assert
        Assert.False(await BlindedHtlcFailures.IsIntroductionForwardAsync(processor, Htlc(s_pathKey)));
    }

    [Fact]
    public void Given_FirstChannelToTheNextNodeUnusable_When_Selecting_Then_TheUsableOneThatCanSendIsChosen()
    {
        // Arrange
        var candidates = new[]
        {
            Info(usable: false, availableMsat: 10_000_000), Info(usable: true, availableMsat: 5_000_000)
        };

        // Act / Assert
        Assert.Equal(1, HtlcSwitch.SelectOutgoingCandidate(candidates, LightningMoney.MilliSatoshis(1_000_000)));
    }

    [Fact]
    public void Given_FirstChannelTooSmall_When_Selecting_Then_TheOneThatCanSendTheAmountIsChosen()
    {
        // Arrange
        var candidates = new[]
        {
            Info(usable: true, availableMsat: 500_000), Info(usable: true, availableMsat: 2_000_000)
        };

        // Act / Assert
        Assert.Equal(1, HtlcSwitch.SelectOutgoingCandidate(candidates, LightningMoney.MilliSatoshis(1_000_000)));
    }

    [Fact]
    public void Given_NoChannelCanSendTheAmount_When_Selecting_Then_TheUsableOneWithTheMostIsChosen()
    {
        // Arrange
        var candidates = new[]
        {
            Info(usable: false, availableMsat: 900_000), Info(usable: true, availableMsat: 300_000),
            Info(usable: true, availableMsat: 600_000)
        };

        // Act / Assert
        Assert.Equal(2, HtlcSwitch.SelectOutgoingCandidate(candidates, LightningMoney.MilliSatoshis(1_000_000)));
    }

    [Fact]
    public void Given_NoUsableChannel_When_Selecting_Then_TheFirstIsChosenForThePolicyToRefuse()
    {
        // Arrange
        var candidates = new[] { Info(usable: false, availableMsat: 0), Info(usable: false, availableMsat: 0) };

        // Act / Assert
        Assert.Equal(0, HtlcSwitch.SelectOutgoingCandidate(candidates, LightningMoney.MilliSatoshis(1_000)));
        Assert.Equal(-1, HtlcSwitch.SelectOutgoingCandidate([], LightningMoney.MilliSatoshis(1_000)));
    }

    private static OutgoingChannelInfo Info(bool usable, ulong availableMsat) =>
        new(TestChannelId, usable, LightningMoney.MilliSatoshis(1), LightningMoney.MilliSatoshis(availableMsat));

    private static HtlcRecord Htlc(CompactPubKey? pathKey) =>
        new(HtlcDirection.Incoming, 7, 1_000_000, new Hash(new byte[32]), 600, HtlcState.RcvdAddAckRevocation,
            OnionRoutingPacket: new byte[OnionConstants.PacketLength], PathKey: pathKey);

    private static IncomingOnionProcessor Processor(HopPayload payload, FeatureSupport routeBlinding)
    {
        var payloads = new Mock<IHopPayloadSerializer>();
        payloads.Setup(p => p.DeserializeAsync(It.IsAny<ReadOnlyMemory<byte>>())).ReturnsAsync(payload);
        var options = new Domain.Node.Options.NodeOptions();
        options.Features.OptionRouteBlinding = routeBlinding;

        // No IRouteBlindingService: the recipient data is never read, only the role
        return new IncomingOnionProcessor(new IntermediateSphinx(), payloads.Object,
                                          new Mock<IOnionReplayStore>().Object,
                                          NullLogger<IncomingOnionProcessor>.Instance, null,
                                          Microsoft.Extensions.Options.Options.Create(options));
    }

    /// <summary>Peels every onion as an intermediate hop with a fixed secret (Moq can't mock span arguments).</summary>
    private sealed class IntermediateSphinx : ISphinxService
    {
        public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                     ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                     OnionPacketKind packetKind) => throw new NotSupportedException();

        public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                           ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                           OnionPacketKind packetKind) =>
            throw new NotSupportedException();

        public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
            throw new NotSupportedException();

        public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                           CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            new(new byte[] { 2, 0 }, s_sharedSecret, new OnionPacket(new byte[OnionConstants.PacketLength]));

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            throw new NotSupportedException();
    }
}