using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Switch;

using Application.Channels.Services;
using Application.Channels.Switch;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="LocalOnlyHtlcSwitch"/> (BOLT2 plan N6-T2): every locked-in incoming HTLC is failed back with the right
/// BOLT 4 failure, idempotently; settled outgoing HTLCs are pruned.
/// </summary>
public class LocalOnlyHtlcSwitchTests
{
    private const uint Height = 812;
    private const ulong AmountMsat = 30_000_000;

    private static readonly Secret s_sharedSecret = SecretOf(0x5E);
    private static readonly byte[] s_reason = Enumerable.Repeat((byte)0xAB, 292).ToArray();

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IFailureOnionService> _failureOnion = new();
    private readonly FakeSphinx _sphinx = new();
    private readonly HtlcRecord _htlc;
    private FailureMessage? _encrypted;

    public LocalOnlyHtlcSwitchTests()
    {
        _htlc = _context.LockIn(HtlcDirection.Incoming, AmountMsat, SecretOf(9));
        _failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                     .Callback((Secret _, FailureMessage message, int _) => _encrypted = message)
                     .Returns(s_reason);
    }

    [Fact]
    public async Task Given_FinalHopOnion_When_LockedIn_Then_SecretRecordedAndFailedWithIncorrectOrUnknownPaymentDetails()
    {
        // Arrange - we have no invoices, so every payment to us is unknown
        _sphinx.Result = new PeeledOnion(new byte[] { 2, 0 }, s_sharedSecret, null);
        var calls = RecordOperationOrder();

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["record", "fail"], calls);
        _operations.Verify(o => o.RecordOnionSecretAsync(TestChannelId, _htlc.Id, s_sharedSecret,
                                                         It.IsAny<CancellationToken>()));
        _failureOnion.Verify(f => f.CreateErrorPacket(s_sharedSecret, It.IsAny<FailureMessage>(), It.IsAny<int>()));
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, _encrypted!.Code);
        Assert.Equal(AmountMsat, _encrypted.HtlcAmount!.MilliSatoshi);
        Assert.Equal(Height, _encrypted.Height);
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _htlc.Id,
                                                It.Is<ReadOnlyMemory<byte>>(r => r.ToArray().SequenceEqual(s_reason)),
                                                It.IsAny<CancellationToken>()));
        Assert.Equal((byte[])_htlc.PaymentHash, _sphinx.AssociatedData);
    }

    [Fact]
    public async Task Given_NoBlockProcessedYet_When_FinalHopLockedIn_Then_FailedWithTemporaryNodeFailure()
    {
        // Arrange - the payer reads the height of incorrect_or_unknown_payment_details; 0 would mislead it
        _sphinx.Result = new PeeledOnion(new byte[] { 2, 0 }, s_sharedSecret, null);

        // Act
        await CreateSwitch(height: 0).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.TemporaryNodeFailure, _encrypted!.Code);
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _htlc.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Given_ForwardingOnion_When_LockedIn_Then_FailedWithTemporaryNodeFailure()
    {
        // Arrange - no forwarding yet
        _sphinx.Result = new PeeledOnion(new byte[] { 2, 0 }, s_sharedSecret, new OnionPacket(Onion));

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.TemporaryNodeFailure, _encrypted!.Code);
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _htlc.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Given_BadOnion_When_LockedIn_Then_FailedMalformedWithItsCodeAndHash()
    {
        // Arrange
        var sha256OfOnion = Enumerable.Repeat((byte)0x33, 32).ToArray();
        _sphinx.Error = new OnionException(FailureCode.InvalidOnionHmac, "bad hmac", sha256OfOnion);

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                         TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailMalformedHtlcAsync(TestChannelId, _htlc.Id, FailureCode.InvalidOnionHmac,
                                                         It.Is<Hash>(h => ((byte[])h).SequenceEqual(sha256OfOnion)),
                                                         It.IsAny<CancellationToken>()));
        _operations.Verify(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_InvalidPayloadWithASharedSecret_When_LockedIn_Then_TheFailureIsEncryptedForTheOrigin()
    {
        // Arrange
        _sphinx.Error = new OnionException(FailureCode.InvalidOnionPayload, "bad framing",
                                           FailureMessage.InvalidOnionPayload(new(0), 0).Data)
        {
            SharedSecret = s_sharedSecret
        };

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, _encrypted!.Code);
        _failureOnion.Verify(f => f.CreateErrorPacket(s_sharedSecret, It.IsAny<FailureMessage>(), It.IsAny<int>()));
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _htlc.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Given_BlindedHtlc_When_LockedIn_Then_FailedMalformedWithInvalidOnionBlinding()
    {
        // Arrange - inside a blinded route every failure is invalid_onion_blinding
        var blinded = _htlc with { PathKey = Point(0x77) };

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, blinded),
                                         TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailMalformedHtlcAsync(TestChannelId, _htlc.Id, FailureCode.InvalidOnionBlinding,
                                                         It.IsAny<Hash>(), It.IsAny<CancellationToken>()));
        Assert.False(_sphinx.Called);
    }

    [Fact]
    public async Task Given_HtlcAlreadyBeingRemoved_When_TheEventIsReplayed_Then_NothingHappens()
    {
        // Arrange - idempotency: the removal was already persisted before a restart
        _context.SetState(_context.State.SendFail(_htlc.Id, s_reason).Next);

        // Act
        await CreateSwitch().HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.False(_sphinx.Called);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_TheOperationIsRefused_When_LockedIn_Then_NoExceptionEscapes()
    {
        // Arrange - e.g. the channel failed meanwhile: the event stays pending for the next start
        _sphinx.Result = new PeeledOnion(new byte[] { 2, 0 }, s_sharedSecret, null);
        _operations.Setup(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                               It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new CommitmentRefusedException("B2-NO-02", "channel failed"));

        // Act
        var exception = await Record.ExceptionAsync(() => CreateSwitch().HandleAsync(
                                                        new IncomingHtlcLockedIn(TestChannelId, _htlc),
                                                        TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task Given_OutgoingHtlcSettled_When_Handled_Then_ItsArchivedRowIsPrunedInOneSave()
    {
        // Arrange - NL-243
        var settled = new OutgoingHtlcSettled(TestChannelId, 4, HashOf(SecretOf(4)), HtlcRemovalKind.Fail);

        // Act
        await CreateSwitch().HandleAsync(settled, TestContext.Current.CancellationToken);

        // Assert
        _context.ChannelStateDbRepository.Verify(
            r => r.PruneSettledHtlcsAsync(TestChannelId,
                                          It.Is<IEnumerable<HtlcKey>>(k => k.Single() ==
                                                                           new HtlcKey(HtlcDirection.Outgoing, 4))));
        Assert.Equal(["save"], _context.Calls);
    }

    private List<string> RecordOperationOrder()
    {
        var calls = new List<string>();
        _operations.Setup(o => o.RecordOnionSecretAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(), It.IsAny<Secret>(),
                                                        It.IsAny<CancellationToken>()))
                   .Callback(() => calls.Add("record"))
                   .Returns(Task.CompletedTask);
        _operations.Setup(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                               It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                   .Callback(() => calls.Add("fail"))
                   .Returns(Task.CompletedTask);
        return calls;
    }

    private LocalOnlyHtlcSwitch CreateSwitch(uint height = Height)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(height);

        return new LocalOnlyHtlcSwitch(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object,
                                       _operations.Object, _failureOnion.Object,
                                       NullLogger<LocalOnlyHtlcSwitch>.Instance,
                                       provider.GetRequiredService<IServiceScopeFactory>(), _sphinx,
                                       blockchainMonitor.Object);
    }

    /// <summary>Moq can't match span arguments: a hand-written peel that returns or throws what the test says.</summary>
    private sealed class FakeSphinx : ISphinxService
    {
        public PeeledOnion? Result { get; set; }
        public OnionException? Error { get; set; }
        public bool Called { get; private set; }
        public byte[]? AssociatedData { get; private set; }

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
                                           CompactPubKey? pathKey, OnionPacketKind packetKind)
        {
            Called = true;
            AssociatedData = associatedData.ToArray();
            if (Error is not null)
                throw Error;

            return Result ?? throw new InvalidOperationException("No peel result set");
        }

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            throw new NotSupportedException();
    }
}