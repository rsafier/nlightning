namespace NLightning.Application.Tests.Onchain.Resolvers;

using Application.Onchain;
using Application.Onchain.Resolvers;
using Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Infrastructure.Crypto.Hashes;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// NL-875 (TR3-P3): the BOLT 5 side of the trampoline relay's HTLC origin (<see cref="TrampolineRelayClaims"/>,
/// <see cref="HtlcUpstreamOutcomeReader"/>, <see cref="FinalHopClaims"/>): an incoming relay part is claimed only with
/// a preimage the relay learnt, the upstream of an outgoing relay HTLC is every incoming part, and a relay part is never
/// decided as our final hop.
/// </summary>
public class TrampolineRelayConsumersTests
{
    private static readonly ChannelId s_outgoingChannelId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());
    private static readonly Secret s_preimage = SecretOf(7);
    private static readonly Hash s_hash = HashOf(s_preimage);

    private readonly Mock<ITrampolineRelayDbRepository> _relays = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly Mock<IInvoiceDbRepository> _invoices = new();

    [Fact]
    public async Task Given_ARelayPartCarryingThePreimage_When_TheResolverAsks_Then_ItsOwnPreimageIsUsed()
    {
        // Arrange
        var context = CreateContext();
        var part = ReplaceIncoming(context, context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage),
                                   r => r with { KnownPreimage = s_preimage });
        UseRelay(TrampolineRelayStatus.Sending, part.Id);

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(context.UnitOfWork.Object, TestChannelId, part,
                                                                         NoOutgoingRecord);

        // Assert
        Assert.Equal((byte[])s_preimage, preimage);
    }

    [Fact]
    public async Task Given_AFulfilledRelay_When_TheResolverAsks_Then_TheRelaysPreimageIsUsed()
    {
        // Arrange
        var context = CreateContext();
        var part = context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);
        UseRelay(TrampolineRelayStatus.Fulfilled, part.Id);

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(context.UnitOfWork.Object, TestChannelId, part,
                                                                         NoOutgoingRecord);

        // Assert
        Assert.Equal((byte[])s_preimage, preimage);
    }

    [Fact]
    public async Task Given_AnOutgoingHtlcOfTheRelayFulfilled_When_TheResolverAsks_Then_ThePreimageItLearntIsUsed()
    {
        // Arrange - the relay itself was not updated yet (a restart between the outgoing fulfill and the relay's save)
        var context = CreateContext();
        var part = context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);
        UseRelay(TrampolineRelayStatus.Sending, part.Id);
        var outgoingKey = new HtlcKey(HtlcDirection.Outgoing, 3);
        context.ChannelStateDbRepository.Setup(r => r.FindHtlcsByOriginAsync(HtlcOrigin.Trampoline(s_hash)))
               .ReturnsAsync([(s_outgoingChannelId, outgoingKey)]);
        var outgoing = new HtlcRecord(HtlcDirection.Outgoing, 3, 29_000_000, s_hash, 560,
                                      HtlcState.RcvdRemoveAckRevocation, HtlcRemoval.Fulfill(s_preimage));

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(
                           context.UnitOfWork.Object, TestChannelId, part,
                           (c, k) => Task.FromResult(c == s_outgoingChannelId && k == outgoingKey ? outgoing : null));

        // Assert
        Assert.Equal((byte[])s_preimage, preimage);
    }

    [Fact]
    public async Task Given_AnIncomingHtlcThatIsNoRelayPart_When_TheResolverAsks_Then_NoPreimage()
    {
        // Arrange
        var context = CreateContext();
        var htlc = context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(context.UnitOfWork.Object, TestChannelId, htlc,
                                                                         NoOutgoingRecord);

        // Assert
        Assert.Null(preimage);
    }

    [Fact]
    public async Task Given_ARelayPartWeFailedOffChain_When_TheResolverAsks_Then_NoPreimage()
    {
        // Arrange
        var context = CreateContext();
        var part = Remove(context, context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage).Id,
                          HtlcRemovalKind.Fail);
        UseRelay(TrampolineRelayStatus.Fulfilled, part.Id);

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(context.UnitOfWork.Object, TestChannelId, part,
                                                                         NoOutgoingRecord);

        // Assert
        Assert.Null(preimage);
    }

    [Fact]
    public async Task Given_ARelayPreimageOfAnotherHash_When_TheResolverAsks_Then_ItIsNeverUsed()
    {
        // Arrange
        var context = CreateContext();
        var part = ReplaceIncoming(context, context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage),
                                   r => r with { KnownPreimage = SecretOf(8) });
        UseRelay(TrampolineRelayStatus.Sending, part.Id);

        // Act
        var preimage = await TrampolineRelayClaims.GetRelayPreimageAsync(context.UnitOfWork.Object, TestChannelId, part,
                                                                         NoOutgoingRecord);

        // Assert
        Assert.Null(preimage);
    }

    [Theory]
    [InlineData(ChannelState.Open, TrampolineRelayStatus.Fulfilled, false)]
    [InlineData(ChannelState.Failed, TrampolineRelayStatus.Sending, false)]
    [InlineData(ChannelState.Failed, TrampolineRelayStatus.Fulfilled, true)]
    public async Task Given_ARelayPartStillLockedIn_When_TheUpstreamIsChecked_Then_ItIsResolvedOnlyOffAnOpenChannelOfACompletedRelay(
        ChannelState incomingState, TrampolineRelayStatus status, bool expected)
    {
        // Arrange
        var context = CreateContext(incomingState);
        var part = context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);
        UseRelay(status, part.Id);

        // Act
        var resolved = await TrampolineRelayClaims.IsUpstreamResolvedAsync(context.UnitOfWork.Object, s_hash);

        // Assert
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task Given_ARelayWhosePartsAreGone_When_TheUpstreamIsChecked_Then_ItIsResolved()
    {
        // Arrange
        var context = CreateContext();
        UseRelay(TrampolineRelayStatus.Sending, 9);

        // Act
        var resolved = await TrampolineRelayClaims.IsUpstreamResolvedAsync(context.UnitOfWork.Object, s_hash);

        // Assert
        Assert.True(resolved);
    }

    [Fact]
    public async Task Given_AnUnknownRelay_When_TheUpstreamIsChecked_Then_TheEventsKeepComing()
    {
        // Arrange
        var context = CreateContext();

        // Act
        var resolved = await TrampolineRelayClaims.IsUpstreamResolvedAsync(context.UnitOfWork.Object, s_hash);

        // Assert
        Assert.False(resolved);
    }

    [Theory]
    [InlineData(HtlcRemovalKind.Fulfill, HtlcUpstreamOutcome.Fulfilled)]
    [InlineData(HtlcRemovalKind.Fail, HtlcUpstreamOutcome.Failed)]
    public async Task Given_ARelayPartWithOurRemoval_When_TheUpstreamOutcomeIsRead_Then_TheRemovalTellsIt(
        HtlcRemovalKind kind, HtlcUpstreamOutcome expected)
    {
        // Arrange
        var context = CreateContext();
        var part = Remove(context, context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage).Id, kind);
        UseRelay(TrampolineRelayStatus.Sending, part.Id);
        UseOutgoingOrigin(context);

        // Act
        var outcome = await HtlcUpstreamOutcomeReader.ReadAsync(context.UnitOfWork.Object, s_outgoingChannelId, 3);

        // Assert
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData(true, TrampolineRelayStatus.Fulfilled, HtlcUpstreamOutcome.Unknown)]
    [InlineData(false, TrampolineRelayStatus.Fulfilled, HtlcUpstreamOutcome.Fulfilled)]
    [InlineData(false, TrampolineRelayStatus.Failed, HtlcUpstreamOutcome.Failed)]
    [InlineData(false, TrampolineRelayStatus.Sending, HtlcUpstreamOutcome.Unknown)]
    public async Task Given_ARelayPartWithoutOurRemoval_When_TheUpstreamOutcomeIsRead_Then_TheRelayDecidesOnceThePartIsGone(
        bool partWaiting, TrampolineRelayStatus status, HtlcUpstreamOutcome expected)
    {
        // Arrange
        var context = CreateContext();
        var partId = partWaiting ? context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage).Id : 9;
        UseRelay(status, partId);
        UseOutgoingOrigin(context);

        // Act
        var outcome = await HtlcUpstreamOutcomeReader.ReadAsync(context.UnitOfWork.Object, s_outgoingChannelId, 3);

        // Assert
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData(TrampolineRelayStatus.Collecting, true)]
    [InlineData(TrampolineRelayStatus.Sending, true)]
    [InlineData(TrampolineRelayStatus.Fulfilled, false)]
    [InlineData(TrampolineRelayStatus.Failed, false)]
    public async Task Given_ARelayPartOnAChannelClosingOnChain_When_AFinalHopDecisionIsAsked_Then_OnlyAnOpenRelayIsAsked(
        TrampolineRelayStatus status, bool expectDecision)
    {
        // Arrange - no invoice for the hash: a plain HTLC would be asked as a possible keysend
        var context = CreateContext(ChannelState.Failed);
        var part = context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);
        UseRelay(status, part.Id);

        // Act
        var decision = await FinalHopClaims.GetFinalHopDecisionAsync(context.UnitOfWork.Object, TestChannelId, part,
                                                                     500);

        // Assert
        Assert.Equal(expectDecision, decision is not null);
        _invoices.Verify(i => i.GetByPaymentHashAsync(It.IsAny<Hash>()), Times.Never);
    }

    private static Task<HtlcRecord?> NoOutgoingRecord(ChannelId channelId, HtlcKey key) =>
        Task.FromResult<HtlcRecord?>(null);

    private NormalOperationTestContext CreateContext(ChannelState state = ChannelState.Open)
    {
        var context = new NormalOperationTestContext(state: state);
        context.UnitOfWork.SetupGet(u => u.TrampolineRelayDbRepository).Returns(_relays.Object);
        context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        context.UnitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(_invoices.Object);
        context.ChannelDbRepository.Setup(r => r.GetByIdAsync(TestChannelId)).ReturnsAsync(context.Channel);
        context.ChannelStateDbRepository.Setup(r => r.FindHtlcsByOriginAsync(It.IsAny<HtlcOrigin>()))
               .ReturnsAsync([]);
        return context;
    }

    private static void UseOutgoingOrigin(NormalOperationTestContext context) =>
        context.ChannelStateDbRepository
               .Setup(r => r.GetHtlcOriginAsync(s_outgoingChannelId, new HtlcKey(HtlcDirection.Outgoing, 3)))
               .ReturnsAsync(HtlcOrigin.Trampoline(s_hash));

    private static HtlcRecord ReplaceIncoming(NormalOperationTestContext context, HtlcRecord record,
                                              Func<HtlcRecord, HtlcRecord> change)
    {
        var state = context.State;
        var changed = change(record);
        context.SetState(ChannelCommitments.Restore(state.ChannelId, state.Params, state.LocalBalanceMsat,
                                                    state.RemoteBalanceMsat,
                                                    state.Htlcs.SetItem(changed.Key, changed).Values,
                                                    state.FeeUpdates, state.LocalNextHtlcId, state.RemoteNextHtlcId,
                                                    state.LocalCommit, state.RemoteCommit, state.RemoteNextCommit,
                                                    state.RemoteNextPerCommitmentPoint));
        return changed;
    }

    /// <summary>Our removal of the incoming HTLC <paramref name="id"/>, sent through the engine.</summary>
    private static HtlcRecord Remove(NormalOperationTestContext context, ulong id, HtlcRemovalKind kind)
    {
        var state = kind == HtlcRemovalKind.Fulfill
                        ? context.State.SendFulfill(id, s_preimage, new Sha256()).Next
                        : context.State.SendFail(id, new byte[] { 1 }).Next;
        context.SetState(state);
        return state.GetHtlc(HtlcDirection.Incoming, id)!;
    }

    private void UseRelay(TrampolineRelayStatus status, ulong partHtlcId)
    {
        var relay = new TrampolineRelayModel(s_hash, Point(0x0B), LightningMoney.MilliSatoshis(29_000_000), 560,
                                             LightningMoney.MilliSatoshis(30_000_000), DateTimeOffset.UnixEpoch);
        if (status is TrampolineRelayStatus.Sending or TrampolineRelayStatus.Fulfilled)
            relay.MarkSending();
        if (status == TrampolineRelayStatus.Fulfilled)
            relay.MarkFulfilled(s_preimage, LightningMoney.MilliSatoshis(1_000_000), DateTimeOffset.UnixEpoch);
        if (status == TrampolineRelayStatus.Failed)
            relay.MarkFailed(0x2002, "no route", DateTimeOffset.UnixEpoch);

        var part = new TrampolineRelayPartModel(s_hash, TestChannelId, partHtlcId,
                                                LightningMoney.MilliSatoshis(30_000_000), 600, SecretOf(0x31),
                                                SecretOf(0x32), null);
        _relays.Setup(r => r.GetAsync(s_hash)).ReturnsAsync((relay, [part]));
        _relays.Setup(r => r.GetPartAsync(TestChannelId, partHtlcId)).ReturnsAsync(part);
    }
}