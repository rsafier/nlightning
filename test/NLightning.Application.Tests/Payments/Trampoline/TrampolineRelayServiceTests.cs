using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Onion;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Switch;

/// <summary>
/// NL-875 TR3-T4: the trampoline relay engine (<see cref="TrampolineRelayService"/>) on <see cref="ThreeNodeHarness"/>
/// (real onions, signatures, switch and SQLite persistence). Alice pays over Bob to Carol, an intermediate trampoline
/// node whose trampoline payload names the next trampoline node (Alice's node id stands in for it); the leg to that
/// node is a fake <see cref="ITrampolineLegSender"/> whose outcome the tests report through
/// <see cref="ITrampolineLegObserver"/>. Every failure Carol sends is read by Alice at the trampoline layer.
/// </summary>
public class TrampolineRelayServiceTests
{
    private const uint IncomingCltvDelta = 700;

    // 1,000,000 msat out: Carol's default fee is 1000 + 1000 ppm = 2,000 msat
    private static readonly LightningMoney s_amountOut = LightningMoney.MilliSatoshis(1_000_000);
    private static readonly LightningMoney s_sumIn = LightningMoney.MilliSatoshis(1_010_000);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();
    private readonly FakeLegSender _legSender = new();

    public TrampolineRelayServiceTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
    }

    private static uint IncomingCltv => ThreeNodeHarness.BlockHeight + IncomingCltvDelta;
    private static uint CltvOut => IncomingCltv - 600;

    [Fact]
    public async Task Given_ASinglePartPayingThePolicy_When_LockedIn_Then_TheLegStartsWithTheRelayInstructions()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);

        // Act
        await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(payment.Hash, leg.PaymentHash);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(CltvOut, leg.FinalCltvExpiry);
        // Our trampoline fee and delta pay for the route: the leg gets the whole difference and keeps Carol's plain
        // forwarding delta (40)
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.Equal(LightningMoney.MilliSatoshis(10_000), leg.MaxFee);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.NotNull(leg.NextTrampolinePacket);
        Assert.Equal(payment.Trampoline.Packet.ToBytes().Length, leg.NextTrampolinePacket.Length);
        Assert.Null(leg.RecipientBlindedPaths);
        Assert.True(leg.AllowMpp);
        // The clock runs (stepped on top of real time): the leg timeout from when the set completed
        var untilDeadline = leg.Deadline - _clock.GetUtcNow();
        Assert.InRange(untilDeadline, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));

        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        var part = Assert.Single(parts);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, part.ChannelId);
        Assert.Equal(payment.Trampoline.SharedSecrets[0], part.TrampolineSharedSecret);
        Assert.Contains(payment.Hash, Engine(harness).SendingPaymentHashes);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_TwoParts_When_OnlyTheFirstArrived_Then_TheLegWaitsForTheTotal()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);

        // Act: the first part is held
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await harness.PumpAsync();

        // Assert
        Assert.Empty(_legSender.Started);
        Assert.Equal(TrampolineRelayStatus.Collecting, (await GetRelayAsync(harness, payment.Hash)).Relay.Status);
        Assert.Contains(payment.Hash, Engine(harness).CollectingPaymentHashes);

        // Act: the second completes the set
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(610_000), s_sumIn);
        await harness.PumpAsync();

        // Assert
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(LightningMoney.MilliSatoshis(10_000), leg.MaxFee);
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(2, parts.Count);
        Assert.Empty(Engine(harness).CollectingPaymentHashes);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_APartWithAnotherTotal_When_ItArrives_Then_OnlyThatPartIsFailedAtTheTrampolineLayer()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await harness.PumpAsync();

        // Act: same hash and trampoline onion, another outer total_msat
        var onion = await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(300_000),
                                       s_sumIn + LightningMoney.MilliSatoshis(1));
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Collecting, relay.Status);
        Assert.Single(parts);
        Assert.Empty(_legSender.Started);
    }

    [Theory]
    [InlineData(1_001_999UL, 600u)]
    [InlineData(1_010_000UL, 575u)]
    public async Task Given_ASetBelowOurPolicy_When_Complete_Then_EveryPartGetsOurPolicyAtTheTrampolineLayer(
        ulong sumInMsat, uint delta)
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness, IncomingCltv - delta);
        var sumIn = LightningMoney.MilliSatoshis(sumInMsat);

        // Act
        var onion = await PayPartAsync(harness, payment, sumIn, sumIn);
        await harness.PumpAsync();

        // Assert: the payer reads trampoline_fee_or_expiry_insufficient with what Carol asks
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TrampolineFeeOrExpiryInsufficient, decrypted.Code);
        Assert.True(decrypted.Message!.TryGetTrampolinePolicy(out var feeBase, out var ppm, out var cltvDelta));
        Assert.Equal(1_000u, feeBase);
        Assert.Equal(1_000u, ppm);
        Assert.Equal((ushort)576, cltvDelta);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)FailureCode.TrampolineFeeOrExpiryInsufficient, relay.FailureCode);
        Assert.Empty(_legSender.Started);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnIncomingExpiryBeyondMaxCltvExpiryDistance_When_Complete_Then_TemporaryTrampolineFailure()
    {
        // Arrange: NL-922, the HTLC expires 700 blocks from now and Carol accepts at most 600 (expiry_too_far); the
        // fee and delta are paid
        await using var harness = await CreateHarnessAsync(routing: r => r.MaxCltvExpiryDistance = 600);
        var payment = await NewPaymentAsync(harness);

        // Act
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert: temporary_trampoline_failure, not NODE|26 (our fee and delta are not what is missing); on 9418c968
        // the leg started
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decrypted.Code);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Contains("too far", relay.FailureReason);
        Assert.Empty(_legSender.Started);
    }

    [Fact]
    public async Task Given_AnOutgoingExpiryWithinExpiryTooSoonBlocks_When_Complete_Then_TemporaryTrampolineFailure()
    {
        // Arrange: NL-922, Carol's chain is 90 blocks ahead: the expiry out (incoming - 600) is 10 blocks away (<= 18)
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight + 90);

        // Act
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert: on 9418c968 the leg started (the expiry was above the height)
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, Decrypt(harness, onion, payment.Trampoline, failed).Code);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Contains("too soon", relay.FailureReason);
        Assert.Empty(_legSender.Started);
    }

    [Fact]
    public async Task Given_OurPolicyRefusedAnAttempt_When_ThePayerRetriesTheHashPayingIt_Then_ANewRelayStarts()
    {
        // Arrange: the first attempt pays too little and gets NODE|26
        await using var harness = await CreateHarnessAsync();
        var first = await NewPaymentAsync(harness);
        var tooLittle = LightningMoney.MilliSatoshis(1_001_000);
        await PayPartAsync(harness, first, tooLittle, tooLittle);
        await harness.PumpAsync();
        Assert.Equal((ushort)FailureCode.TrampolineFeeOrExpiryInsufficient,
                     (await GetRelayAsync(harness, first.Hash)).Relay.FailureCode);

        // Act: the payer retries the same payment hash with a new trampoline onion and enough fee
        var retry = await NewPaymentAsync(harness, samePreimage: first.Preimage);
        await PayPartAsync(harness, retry, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert: the failed relay made way for the new one
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(first.Hash, leg.PaymentHash);
        var (relay, parts) = await GetRelayAsync(harness, first.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(s_sumIn, Assert.Single(parts).Amount);
        Assert.Single(harness.Alice.PaymentHandler.Failed);

        // NL-899: the failed first attempt stays in the history listforwards reads
        var replaced = await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.ListReplacedAttemptsAsync(
                                                            new TrampolineRelayListQuery(0, 10),
                                                            TestContext.Current.CancellationToken));
        var attempt = Assert.Single(replaced);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal(first.Hash, attempt.PaymentHash);
        Assert.Equal((ushort)FailureCode.TrampolineFeeOrExpiryInsufficient, attempt.FailureCode);
        Assert.Equal(tooLittle, attempt.IncomingAmount);
        Assert.Equal(1, attempt.Parts);
    }

    [Fact]
    public async Task Given_TheLegSucceeds_When_TheLinkIsDown_Then_ThePreimageIsKeptAndTheReplayFulfillsAndBooksOnce()
    {
        // Arrange: two parts, the leg started, then Bob goes away
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(610_000), s_sumIn);
        await harness.PumpAsync();
        Assert.Single(_legSender.Started);
        harness.Disconnect(harness.Bob, harness.Carol);

        // Act: the leg cost 1,005,000 msat (5,000 msat of routing fees)
        await Observer(harness).OnLegSucceededAsync(payment.Hash, payment.Preimage,
                                                    LightningMoney.MilliSatoshis(1_005_000),
                                                    TestContext.Current.CancellationToken);

        // Assert: Fulfilled with the fee, the preimage on both incoming records, one accounting event
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(payment.Preimage, relay.Preimage);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), relay.FeeEarned);
        var incoming = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                              .Where(h => h.Direction == HtlcDirection.Incoming).ToList();
        Assert.Equal(2, incoming.Count);
        Assert.All(incoming, h => Assert.Equal(payment.Preimage, h.KnownPreimage));
        Assert.Equal(1, await CountSettledEventsAsync(harness, payment.Hash));

        // Act: the leg reports again (a restart's reconciliation) and the link comes back
        await Observer(harness).OnLegSucceededAsync(payment.Hash, payment.Preimage,
                                                    LightningMoney.MilliSatoshis(1_005_000),
                                                    TestContext.Current.CancellationToken);
        await harness.ReconnectLinkAsync(harness.Bob, harness.Carol);
        await harness.PumpAsync();

        // Assert: both parts fulfilled, booked once
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.All(harness.Alice.PaymentHandler.Fulfilled, f => Assert.Equal(payment.Preimage, f.PaymentPreimage));
        Assert.Equal(1, await CountSettledEventsAsync(harness, payment.Hash));
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnOutgoingHtlcInFlight_When_TheLegReportsAFailure_Then_NoPartFailsUntilItIsResolved()
    {
        // Arrange: the leg offers a real HTLC from Carol to Alice (origin 3), which Alice holds
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        _legSender.OnStart = request => OfferOutgoingAsync(harness, request);
        harness.Alice.SwitchSuspended = true;
        var payment = await NewPaymentAsync(harness);
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();
        Assert.Single(_legSender.Started);
        Assert.Single(_legSender.Offered);

        // Act: the leg says it failed while its HTLC is still unresolved
        var failure = new TrampolineLegFailure(TrampolineLegFailureKind.RouteFailure, null, null, false, "retries");
        await Observer(harness).OnLegFailedAsync(payment.Hash, failure, TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert: nothing failed upstream
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(TrampolineRelayStatus.Sending, (await GetRelayAsync(harness, payment.Hash)).Relay.Status);

        // Act: Alice fails the outgoing HTLC back (an unreadable onion), then the leg reports again
        harness.Alice.SwitchSuspended = false;
        await harness.Alice.ReplayPendingEventsAsync();
        await harness.PumpAsync();
        Assert.Single(_legSender.OutgoingFailed);
        await Observer(harness).OnLegFailedAsync(payment.Hash, failure, TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure,
                     Decrypt(harness, onion, payment.Trampoline, failed).Code);
        Assert.Equal(TrampolineRelayStatus.Failed, (await GetRelayAsync(harness, payment.Hash)).Relay.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnOutgoingHtlcFulfilled_When_TheLegSenderReportsNothing_Then_TheRelayFulfillsFromTheSwitch()
    {
        // Arrange: the leg's HTLC from Carol to Alice, fulfilled by Alice with the payment's preimage
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        _legSender.OnStart = request => OfferOutgoingAsync(harness, request);
        harness.Alice.SwitchSuspended = true;
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();
        var outgoingId = Assert.Single(_legSender.Offered);

        // Act (Alice's switch handles her own payment's events again from here)
        await harness.Alice.Operations.FulfillHtlcAsync(ThreeNodeHarness.CarolAliceChannelId, outgoingId,
                                                        payment.Preimage, TestContext.Current.CancellationToken);
        harness.Alice.SwitchSuspended = false;
        await harness.PumpAsync();

        // Assert: the switch's origin-3 fulfill fulfilled the relay (no payment row: the fee is what the leg owed)
        Assert.Single(_legSender.OutgoingFulfilled);
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(payment.Preimage, fulfilled.PaymentPreimage);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.Equal(s_sumIn - s_amountOut, relay.FeeEarned);
        Assert.Equal(1, await CountSettledEventsAsync(harness, payment.Hash));
    }

    [Fact]
    public async Task Given_TheNextTrampolineFails_When_TheLegReportsIt_Then_TheOriginReadsItAtTheNextTrampolinesIndex()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();
        Assert.Single(_legSender.Started);

        // The next trampoline node (hop 1 of the trampoline onion) refused the payment; our leg unwrapped its outer
        // layer, which leaves its trampoline layer
        var downstream = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                                .CreateErrorPacket(payment.Trampoline.SharedSecrets[1],
                                                   FailureMessage.IncorrectOrUnknownPaymentDetails(s_amountOut, 500));

        // Act
        await Observer(harness).OnLegFailedAsync(payment.Hash,
                                                 new TrampolineLegFailure(
                                                     TrampolineLegFailureKind.DownstreamTrampolineError, downstream,
                                                     null, true, "the next trampoline refused it"),
                                                 TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Null(relay.FailureCode);
        Assert.Empty(Engine(harness).SendingPaymentHashes);
        AssertNoHtlcs(harness);
    }

    [Theory]
    [InlineData(TrampolineLegFailureKind.UnknownNextNode, FailureCode.UnknownNextTrampoline)]
    [InlineData(TrampolineLegFailureKind.NoRoute, FailureCode.TemporaryTrampolineFailure)]
    [InlineData(TrampolineLegFailureKind.RouteFailure, FailureCode.TemporaryTrampolineFailure)]
    [InlineData(TrampolineLegFailureKind.Timeout, FailureCode.TemporaryTrampolineFailure)]
    public async Task Given_OurLegFails_When_Reported_Then_OurOwnErrorAtOurIndex(TrampolineLegFailureKind kind,
                                                                                 FailureCode expected)
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Act
        await Observer(harness).OnLegFailedAsync(payment.Hash,
                                                 new TrampolineLegFailure(kind, null, null, false, kind.ToString()),
                                                 TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(expected, decrypted.Code);
        Assert.Equal((ushort)expected, (await GetRelayAsync(harness, payment.Hash)).Relay.FailureCode);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_AnIncompleteSet_When_TheMppTimeoutPasses_Then_EveryPartGetsMppTimeoutAtTheTrampolineLayer()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        var onion = await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await harness.PumpAsync();

        // Act: not at 59 s, at 60 s
        await AdvanceAsync(harness, TimeSpan.FromSeconds(59));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(1));

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, payment.Trampoline, failed);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
        Assert.Equal(TrampolineRelayStatus.Failed, (await GetRelayAsync(harness, payment.Hash)).Relay.Status);
        Assert.Empty(_legSender.Started);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_TheRelaysInFlightLimit_When_AnotherSetCompletes_Then_ItGetsTemporaryTrampolineFailure()
    {
        // Arrange: one relay in flight at most
        await using var harness = await CreateHarnessAsync(o => o.MaxRelaysInFlight = 1);
        var first = await NewPaymentAsync(harness);
        var second = await NewPaymentAsync(harness);
        await PayPartAsync(harness, first, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Act
        var onion = await PayPartAsync(harness, second, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(first.Hash, Assert.Single(_legSender.Started).PaymentHash);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(second.Hash, failed.PaymentHash);
        var decrypted = Decrypt(harness, onion, second.Trampoline, failed);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decrypted.Code);
        Assert.Equal(TrampolineRelayStatus.Sending, (await GetRelayAsync(harness, first.Hash)).Relay.Status);
    }

    [Fact]
    public async Task Given_CarolRestartsWhileCollecting_When_TheLastPartArrives_Then_TheLegStartsOnce()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await harness.PumpAsync();

        // Act: a new engine over the same database; the replayed lock-in rearms the mpp_timeout
        await harness.RestartAsync(harness.Carol);
        await Engine(harness).StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        Assert.Contains(payment.Hash, Engine(harness).CollectingPaymentHashes);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(610_000), s_sumIn);
        await harness.PumpAsync();

        // Assert
        Assert.Single(_legSender.Started);
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(2, parts.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_CarolRestartsWhileSending_When_TheLegThenSucceeds_Then_NoSecondLegAndBothPartsFulfilled()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(610_000), s_sumIn);
        await harness.PumpAsync();
        Assert.Single(_legSender.Started);

        // Act: restart, the startup hook and the replays find the relay Sending
        await harness.RestartAsync(harness.Carol);
        await Engine(harness).StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert
        Assert.Single(_legSender.Started);
        Assert.Contains(payment.Hash, Engine(harness).SendingPaymentHashes);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);

        // Act: the leg's reconciliation reports the success to the new engine
        await Observer(harness).OnLegSucceededAsync(payment.Hash, payment.Preimage,
                                                    LightningMoney.MilliSatoshis(1_004_000),
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(2, harness.Alice.PaymentHandler.Fulfilled.Count);
        Assert.Equal(LightningMoney.MilliSatoshis(6_000), (await GetRelayAsync(harness, payment.Hash)).Relay.FeeEarned);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ThePartOfferedTwice_When_TheIngressHandlesIt_Then_OnePartAndNothingFailed()
    {
        // Arrange: one part held (half the total)
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000), s_sumIn);
        await harness.PumpAsync();
        var htlc = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                          .Single(h => h is { Direction: HtlcDirection.Incoming, Removal: null });
        var onion = await harness.Carol.Services.GetRequiredService<IncomingOnionProcessor>()
                                 .ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash, null, htlc.PathKey,
                                               LightningMoney.MilliSatoshis(htlc.AmountMsat), htlc.CltvExpiry);
        var relayOnion = Assert.IsType<IncomingOnionTrampolineRelay>(onion);
        var lockedIn = new IncomingHtlcLockedIn(ThreeNodeHarness.BobCarolChannelId, htlc);

        // Act: offered again (a part not yet known to be saved, as after a restart) and replayed
        await Engine(harness).HandleNewPartAsync(lockedIn, relayOnion, TestContext.Current.CancellationToken);
        await Engine(harness).HandleNewPartAsync(lockedIn, relayOnion, TestContext.Current.CancellationToken);
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();

        // Assert
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Collecting, relay.Status);
        Assert.Single(parts);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(_legSender.Started);
    }

    [Fact]
    public async Task Given_ASendingRelayPastItsDeadline_When_NoOutgoingHtlcIsInFlight_Then_TheWatchdogFailsIt()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness);
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();
        Assert.Single(_legSender.Started);

        // Act: the leg's deadline (60 s) plus one leg timeout without an outcome
        await AdvanceAsync(harness, TimeSpan.FromSeconds(119));
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        await AdvanceAsync(harness, TimeSpan.FromSeconds(1));

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure,
                     Decrypt(harness, onion, payment.Trampoline, failed).Code);
        Assert.Equal(TrampolineRelayStatus.Failed, (await GetRelayAsync(harness, payment.Hash)).Relay.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_NoLegSender_When_APartArrives_Then_TemporaryTrampolineFailureAndNothingStored()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync(registerLegSender: false);
        var payment = await NewPaymentAsync(harness);

        // Act
        var onion = await PayPartAsync(harness, payment, s_sumIn, s_sumIn);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure,
                     Decrypt(harness, onion, payment.Trampoline, failed).Code);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
    }

    #region Helpers

    private Task<ThreeNodeHarness> CreateHarnessAsync(Action<TrampolineOptions>? configure = null,
                                                      bool registerLegSender = true, bool carolAlice = false,
                                                      Action<RoutingOptions>? routing = null) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Carol.Options.Features.OptionTrampolineRouting = FeatureSupport.Optional;
            h.Carol.Options.Features.AllowExperimentalFeatures = true;
            routing?.Invoke(h.Carol.Options.Routing);
            h.Carol.ConfigureServices = services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
                services.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
                services.Configure<HtlcSwitchOptions>(o => o.BlindedErrorMaxDelay = TimeSpan.Zero);
                services.AddTrampolineRelayServices();
                if (configure is not null)
                    services.Configure(configure);
                if (registerLegSender)
                    services.AddSingleton<ITrampolineLegSender>(_legSender);
            };
        }, carolAlice: carolAlice);

    /// <summary>The leg's one HTLC, Carol to Alice, carrying the relay's origin and an onion Alice cannot read.</summary>
    private async Task OfferOutgoingAsync(ThreeNodeHarness harness, TrampolineLegRequest request)
    {
        var packet = RandomNumberGenerator.GetBytes(OnionConstants.PacketLength);
        packet[0] = 0;
        var id = await harness.Carol.Operations.OfferHtlcAsync(ThreeNodeHarness.CarolAliceChannelId, request.Amount,
                                                               request.PaymentHash, request.MaxFirstHopCltvExpiry,
                                                               new OnionPacket(packet), null,
                                                               HtlcOrigin.Trampoline(request.PaymentHash));
        _legSender.RecordOffered(id);
    }

    private static TrampolineRelayService Engine(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<TrampolineRelayService>();

    private static ITrampolineLegObserver Observer(ThreeNodeHarness harness) =>
        harness.Carol.Services.GetRequiredService<ITrampolineLegObserver>();

    private async Task AdvanceAsync(ThreeNodeHarness harness, TimeSpan by)
    {
        _clock.Advance(by);
        await Engine(harness).WhenIdleAsync();
        await harness.PumpAsync();
    }

    /// <summary>
    /// A payment Alice routes through Carol to the next trampoline node (Alice's id): the trampoline onion's first
    /// hop (Carol) forwards <see cref="s_amountOut"/> at <paramref name="cltvOut"/>; the outer onion's secret is shared
    /// by every part.
    /// </summary>
    private static async Task<RelayPayment> NewPaymentAsync(ThreeNodeHarness harness, uint? cltvOut = null,
                                                            Secret? samePreimage = null)
    {
        var preimage = samePreimage ?? new Secret(RandomNumberGenerator.GetBytes(32));
        var hash = new Hash(SHA256.HashData((byte[])preimage));
        var outgoingCltv = cltvOut ?? CltvOut;
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var (nodeId, payload) in new[]
                 {
                     (harness.Carol.NodeId,
                      new HopPayload(new AmtToForwardTlv(s_amountOut), new OutgoingCltvValueTlv(outgoingCltv),
                                     new OutgoingNodeIdTlv(harness.Alice.NodeId))),
                     (harness.Alice.NodeId,
                      new HopPayload(new AmtToForwardTlv(s_amountOut), new OutgoingCltvValueTlv(outgoingCltv),
                                     new PaymentDataTlv(RandomNumberGenerator.GetBytes(32), s_amountOut)))
                 })
        {
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            hops.Add(new OnionHop(nodeId, stream.ToArray()));
        }

        var trampoline = harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                                .Build(hops, RandomNumberGenerator.GetBytes(32), hash,
                                       TrampolineOnionSizePolicy.Auto(650));
        return new RelayPayment(preimage, hash, trampoline, RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC of <paramref name="part"/> expiring at <see cref="IncomingCltv"/>, whose outer
    /// final payload promises <paramref name="outerTotal"/> and carries the payment's trampoline onion.
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, RelayPayment payment,
                                                         LightningMoney part, LightningMoney outerTotal)
    {
        var route = harness.RouteToCarol(part, payment.Hash, new Secret(payment.OuterSecret), IncomingCltvDelta);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            var payload = hop.IsFinal
                              ? new HopPayload(new AmtToForwardTlv(hop.AmountToForward),
                                               new OutgoingCltvValueTlv(hop.OutgoingCltvValue),
                                               new PaymentDataTlv(payment.OuterSecret, outerTotal),
                                               new TrampolineOnionPacketTlv(payment.Trampoline.Packet))
                              : PaymentOnionFactory.CreatePayload(hop, route);
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            hops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        var constructed = harness.Alice.Services.GetRequiredService<ISphinxService>()
                                 .ConstructWithSharedSecrets(hops, new PrivKey(PaymentOnionFactory.CreateSessionKey()),
                                                             route.PaymentHash);
        var onion = new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
        await harness.Alice.Operations.OfferHtlcAsync(ThreeNodeHarness.AliceBobChannelId, route.FirstHopAmount,
                                                      route.PaymentHash, route.FirstHopCltvExpiry, onion.Packet,
                                                      null, HtlcOrigin.Local(route.PaymentHash));
        return onion;
    }

    private static async Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)>
        GetRelayAsync(ThreeNodeHarness harness, Hash paymentHash)
    {
        var stored = await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(paymentHash));
        Assert.NotNull(stored);
        return stored.Value;
    }

    private static Task<int> CountSettledEventsAsync(ThreeNodeHarness harness, Hash paymentHash) =>
        harness.Carol.InScopeAsync(async u =>
            (await u.AccountingEventDbRepository.GetUnsealedAsync(1_000, TestContext.Current.CancellationToken))
           .Count(e => e.EventKey == AccountingEventKeys.TrampolineRelaySettled(paymentHash)));

    private static TrampolineDecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion,
                                                      TrampolineOnion trampoline, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<ITrampolineFailureOnionService>()
                               .DecryptTrampolineErrorPacket(onion.SharedSecrets, trampoline.SharedSecrets,
                                                             failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
    }

    private sealed record RelayPayment(Secret Preimage, Hash Hash, TrampolineOnion Trampoline, byte[] OuterSecret);

    /// <summary>Records the legs the engine starts; their outcomes are reported by the tests.</summary>
    private sealed class FakeLegSender : ITrampolineLegSender
    {
        private readonly List<TrampolineLegRequest> _started = [];
        private readonly List<ulong> _offered = [];
        private readonly List<OutgoingHtlcFulfilled> _fulfilled = [];
        private readonly List<OutgoingHtlcFailed> _failed = [];

        /// <summary>Runs when a leg starts (e.g. offers a real HTLC).</summary>
        public Func<TrampolineLegRequest, Task>? OnStart { get; set; }

        public IReadOnlyList<ulong> Offered => Snapshot(_offered);
        public IReadOnlyList<OutgoingHtlcFulfilled> OutgoingFulfilled => Snapshot(_fulfilled);
        public IReadOnlyList<OutgoingHtlcFailed> OutgoingFailed => Snapshot(_failed);

        public void RecordOffered(ulong htlcId)
        {
            lock (_offered)
                _offered.Add(htlcId);
        }

        public IReadOnlyList<TrampolineLegRequest> Started
        {
            get
            {
                lock (_started)
                    return _started.ToList();
            }
        }

        public Task StartAsync(TrampolineLegRequest request, CancellationToken cancellationToken)
        {
            lock (_started)
                _started.Add(request);
            return OnStart?.Invoke(request) ?? Task.CompletedTask;
        }

        public Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken)
        {
            lock (_fulfilled)
                _fulfilled.Add(fulfilled);
            return Task.CompletedTask;
        }

        public Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken)
        {
            lock (_failed)
                _failed.Add(failed);
            return Task.CompletedTask;
        }

        private static IReadOnlyList<T> Snapshot<T>(List<T> list)
        {
            lock (list)
                return list.ToList();
        }
    }

    #endregion
}