using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Channels.RoutingPolicies;
using Application.Payments.Onion;
using Application.Payments.Routing;
using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Channels.Harness;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Switch;

/// <summary>
/// NL-895: Carol relays a blinded trampoline hop (a BOLT 12 recipient that supports trampoline made her a hop of its
/// blinded path) with the production relay engine on <see cref="ThreeNodeHarness"/> (real onions, route blinding and
/// SQLite). Alice is both the payer (over Bob) and the recipient whose blinded path names Carol's next hop by
/// <c>short_channel_id</c> (D-NL895-1) and fixes Carol's price in <c>payment_relay</c> (D-NL895-2), cheaper in fee and
/// delta than Carol's <c>Node:Trampoline</c> policy, so a NODE|26 would show. The leg is a fake that records what the
/// engine asks; every failure is read by Alice. NL-922 (D-NL922-1): the <c>payment_relay</c> must still be at least
/// Carol's policy for the hop (the named channel's, <c>Node:Routing</c> for a <c>next_node_id</c> hop), here
/// <c>Node:Routing</c> = 100 msat + 10 ppm, delta 40, which the default <c>payment_relay</c> pays exactly as a recipient
/// building it from Carol's <c>channel_update</c> would.
/// </summary>
public class BlindedTrampolineRelayTests
{
    private const uint IncomingCltvDelta = 700;

    // Carol's price as the recipient set it: 100 msat + 10 ppm, delta 50 (her Node:Trampoline asks 1000 + 1000 ppm,
    // delta 576; her plain forwarding delta is 40)
    private static readonly BlindedPaymentRelay s_carolRelay = new(50, 10, 100);

    // The payment's outer total and what payment_relay leaves of it: ceil((1,000,200 - 100) * 10^6 / 1,000,010)
    private static readonly LightningMoney s_total = LightningMoney.MilliSatoshis(1_000_200);
    private static readonly LightningMoney s_amountOut = LightningMoney.MilliSatoshis(1_000_090);

    // A scid of the Carol-Alice channel that a splice retired (resolved through IRetiredScidMap)
    private static readonly ShortChannelId s_retiredScid = new(399, 7, 0);

    private readonly SteppedTimeProvider _clock = new();
    private readonly Mock<IBlockchainMonitor> _carolMonitor = new();
    private readonly FakeLegSender _legSender = new();
    private readonly RecordingLoggerProvider _carolLogs = new();
    private readonly SwitchableRetiredScidMap _retiredScids = new();

    public BlindedTrampolineRelayTests()
    {
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight);
    }

    private static uint IncomingCltv => ThreeNodeHarness.BlockHeight + IncomingCltvDelta;
    private static uint FinalCltv => IncomingCltv - 200;

    #region Next node (D-NL895-1)

    [Fact]
    public async Task Given_ARecipientDataNamingOurChannelByItsScid_When_TheSetCompletes_Then_TheLegGoesToItsPeer()
    {
        // Arrange: Carol is the introduction node; her data names the Carol-Alice channel by its real scid
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: the leg pays Alice what payment_relay leaves, at the outer expiry minus its delta, with the whole
        // difference as its fee and Carol's plain forwarding delta kept
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - s_carolRelay.CltvExpiryDelta, leg.FinalCltvExpiry);
        Assert.Equal(s_total - s_amountOut, leg.MaxFee);
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.True(leg.AllowMpp);
        Assert.Null(leg.RecipientBlindedPaths);

        // Assert: Alice (the recipient) peels the next trampoline packet with the next path key
        Assert.NotNull(leg.NextPathKey);
        Assert.NotNull(leg.NextTrampolinePacket);
        var peeled = harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                            .Peel(leg.NextTrampolinePacket, payment.Hash, leg.NextPathKey);
        Assert.True(peeled.IsFinal);

        // Assert: the resolved node is stored for a restart
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(harness.Alice.NodeId, relay.NextNodeId);
        Assert.Equal((byte[])leg.NextPathKey.Value, relay.NextPathKey);
        Assert.Single(parts);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_ARecipientDataNamingOurChannelByAnAlias_When_TheSetCompletes_Then_TheLegGoesToItsPeer()
    {
        // Arrange: Bob-Carol requires option_scid_alias; the data names it by Carol's alias
        await using var harness = await CreateHarnessAsync(bobCarolScidAlias: FeatureSupport.Compulsory);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.BobCarolCarolAlias),
                                            introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total, bobCarolScid: ThreeNodeHarness.BobCarolCarolAlias);
        await harness.PumpAsync();

        // Assert
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Bob.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
    }

    [Fact]
    public async Task Given_AnUnknownScidAtTheIntroductionNode_When_ThePartArrives_Then_OurOwnInvalidOnionBlinding()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness, CarolData(new ShortChannelId(999, 9, 9)), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: Carol's own error at the trampoline layer, nothing stored, no leg, refused by the scid check itself
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
        AssertPartRefused("short_channel_id 999x9x9, which is none of our open channels");
    }

    [Fact]
    public async Task Given_AnUnknownScidPastTheIntroductionNode_When_ThePartArrives_Then_MalformedWithThePacketHash()
    {
        // Arrange: Bob introduces the path, Carol gets her path key in the outer payload
        await using var harness = await CreateHarnessAsync();
        var payment = await NewPaymentAsync(harness, CarolData(new ShortChannelId(999, 9, 9)), introduction: false);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: update_fail_malformed_htlc + invalid_onion_blinding with the trampoline packet's sha256 (PR 836)
        var malformed = Assert.Single(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                                     && s.Message is UpdateFailMalformedHtlcMessage);
        var payload = ((UpdateFailMalformedHtlcMessage)malformed.Message).Payload;
        Assert.Equal((ushort)FailureCode.InvalidOnionBlinding, payload.FailureCode);
        Assert.Equal(SHA256.HashData(payment.Trampoline.Packet.ToBytes()), payload.Sha256OfOnion.ToArray());
        Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
        AssertPartRefused("short_channel_id 999x9x9, which is none of our open channels");
    }

    #endregion

    #region Policy (D-NL895-2)

    [Fact]
    public async Task Given_TwoPartsOfTheTotal_When_TheSetCompletes_Then_PaymentRelayAppliesToTheWholeSet()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act: 400,000 then 600,200 msat, each promising the outer total
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();
        Assert.Empty(_legSender.Started);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();

        // Assert: one leg for the whole set, at payment_relay over the total (not over each part)
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - s_carolRelay.CltvExpiryDelta, leg.FinalCltvExpiry);
        Assert.Equal(s_total - s_amountOut, leg.MaxFee);
        Assert.Equal(2, (await GetRelayAsync(harness, payment.Hash)).Parts.Count);
    }

    [Fact]
    public async Task Given_CarolRestartsWhileCollecting_When_TheLastPartArrives_Then_TheLegGoesToTheResolvedNode()
    {
        // Arrange: the first part of a scid-named blinded hop, then Carol restarts from her database
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();
        Assert.Equal(harness.Alice.NodeId, (await GetRelayAsync(harness, payment.Hash)).Relay.NextNodeId);

        // Act
        await harness.RestartAsync(harness.Carol);
        await harness.Carol.Services.GetRequiredService<TrampolineRelayService>()
                   .StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();

        // Assert: one leg, to the node resolved before the restart, at payment_relay's price
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.NotNull(leg.NextPathKey);
        Assert.Equal(2, (await GetRelayAsync(harness, payment.Hash)).Parts.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_APaymentRelayDeltaBelowTheHopsDelta_When_ThePartArrives_Then_RefusedBeforeItJoins()
    {
        // Arrange: delta 20 < Carol's 40 (NL-922: checked per part, as a blinded forward's payment_relay)
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid, new BlindedPaymentRelay(20, 10, 100));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: our own invalid_onion_blinding, never NODE|26; no relay row, no MaxRelaysInFlight slot
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion, "cltv_expiry_delta 20 is below our 40");
    }

    [Fact]
    public async Task Given_AnOuterExpiryAboveTheHtlcs_When_ThePartArrives_Then_InvalidOnionBlinding()
    {
        // Arrange: the outer payload promises an expiry 10 blocks above the HTLC's
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total, outerCltv: IncomingCltv + 10);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, Decrypt(harness, onion, payment.Trampoline).Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PaymentConstraintsViolated_When_ThePartArrives_Then_TheBlindedAnswerForOurRole(
        bool introduction)
    {
        // Arrange: max_cltv_expiry below the outer expiry
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid,
                             constraints: new BlindedPaymentConstraints(IncomingCltv - 1, 1));
        var payment = await NewPaymentAsync(harness, data, introduction);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: at the introduction node our own invalid_onion_blinding, past it update_fail_malformed_htlc
        Assert.Empty(_legSender.Started);
        if (introduction)
        {
            Assert.Equal(FailureCode.InvalidOnionBlinding, Decrypt(harness, onion, payment.Trampoline).Code);
            Assert.DoesNotContain(harness.Sent, s => s.Message is UpdateFailMalformedHtlcMessage);
        }
        else
        {
            Assert.Contains(harness.Sent, s => s is { From: "Carol", To: "Bob" }
                                            && s.Message is UpdateFailMalformedHtlcMessage);
        }
    }

    #endregion

    #region Added after our shutdown (NL-921)

    [Fact]
    public async Task Given_ARelayPartAtTheIntroductionNodeAddedAfterOurShutdown_When_Handled_Then_OurOwnInvalidOnionBlinding()
    {
        // Arrange: the part is locked in at Carol while her switch waits, then Carol is found to have sent shutdown
        // on Bob-Carol before Bob added it (NL-279): it is failed back, never relayed
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);
        harness.Carol.SwitchSuspended = true;
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();
        var channel = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId);
        var incoming = Assert.Single(channel.Commitments!.Htlcs.Values, h => h.Direction == HtlcDirection.Incoming);
        channel.SetLocalShutdownScript(new BitcoinScript([0x00, 0x14, .. Enumerable.Repeat((byte)0xC0, 20)]));
        channel.SetFirstRemoteHtlcIdAfterLocalShutdown(incoming.Id);
        Assert.True(channel.IsRemoteHtlcAddedAfterLocalShutdown(incoming.Id));

        // Act
        harness.Carol.SwitchSuspended = false;
        await harness.Carol.ReplayPendingEventsAsync();
        await harness.PumpAsync();

        // Assert: the introduction node's own invalid_onion_blinding at the trampoline layer (TR-R-14), not
        // temporary_node_failure; no relay, no leg
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
    }

    #endregion

    #region Price floor (NL-922, D-NL922-1)

    [Fact]
    public async Task Given_APaymentRelayFeeBelowTheChannelsPolicy_When_ThePartArrives_Then_InvalidOnionBlinding()
    {
        // Arrange: Carol's setchannelpolicy asks 200 msat on the Carol-Alice channel, and the grace of her former
        // Node:Routing policy (100 msat) has passed; the recipient's payment_relay pays 100 msat + 10 ppm
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, feeBaseMsat: 200);
        PassThePolicyGracePeriod();
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: no free relay through us (on 9418c968 the leg started at the recipient's price)
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              "payment_relay fee 100 msat + 10 ppm is below our policy 200 msat");
    }

    [Fact]
    public async Task Given_AFreePaymentRelay_When_ThePartArrives_Then_NoFreeRebalanceThroughUs()
    {
        // Arrange: payment_relay (0, 0, 40), the review's free circular rebalance
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid, new BlindedPaymentRelay(40, 0, 0));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              "payment_relay fee 0 msat + 0 ppm is below our policy 100 msat");
    }

    [Fact]
    public async Task Given_APaymentRelayOfTheReplacedPolicy_When_WithinTheGracePeriod_Then_TheLegStarts()
    {
        // Arrange: Carol just raised the Carol-Alice channel to 200 msat and delta 80; the recipient's payment_relay
        // still has her former channel_update (100 msat + 10 ppm, delta 50 >= the former 40)
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, feeBaseMsat: 200, cltvExpiryDelta: 80);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: accepted as a forward would be (BOLT 7 grace), keeping the most lenient delta in grace
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_AChannelDeltaBelowTheNodes_When_ThePaymentRelayPaysIt_Then_TheLegKeepsTheChannelsDelta()
    {
        // Arrange: setchannelpolicy delta 34 on the Carol-Alice channel (Node:Routing: 40); the recipient built its
        // payment_relay from that channel_update with 36
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, cltvExpiryDelta: 34);
        PassThePolicyGracePeriod();
        var data = CarolData(ThreeNodeHarness.CarolAliceScid, new BlindedPaymentRelay(36, 10, 100));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: on 9418c968 refused (36 < the node's 40); the leg may expire as late as the channel allows
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(IncomingCltv - 36, leg.FinalCltvExpiry);
        Assert.Equal(IncomingCltv - 34, leg.MaxFirstHopCltvExpiry);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_AChannelDeltaAboveTheNodes_When_ThePaymentRelayPaysOnlyTheNodes_Then_InvalidOnionBlinding()
    {
        // Arrange: setchannelpolicy delta 80 on the Carol-Alice channel, past the grace; payment_relay delta 50
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, cltvExpiryDelta: 80);
        PassThePolicyGracePeriod();
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: on 9418c968 accepted (50 >= the node's 40) with a first hop expiring 10 blocks too late
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion, "cltv_expiry_delta 50 is below our 80");
    }

    [Fact]
    public async Task Given_ANextNodeIdHopBelowOurNodePolicy_When_ThePartArrives_Then_InvalidOnionBlinding()
    {
        // Arrange: no channel named, so Node:Routing (100 msat + 10 ppm) is the floor; payment_relay asks 50 msat
        await using var harness = await CreateHarnessAsync();
        var data = CarolDataToNode(harness.Alice.NodeId, new BlindedPaymentRelay(50, 10, 50));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              "payment_relay fee 50 msat + 10 ppm is below our policy 100 msat");
    }

    [Fact]
    public async Task Given_ANextNodeIdHopPayingOurNodePolicy_When_AChannelToThatNodeAsksMore_Then_TheLegStarts()
    {
        // Arrange: the Carol-Alice channel asks 200 msat, but a next_node_id hop names no channel: Node:Routing
        // (never Node:Trampoline, 1000 msat + 1000 ppm) is its policy
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, feeBaseMsat: 200);
        PassThePolicyGracePeriod();
        var payment = await NewPaymentAsync(harness, CarolDataToNode(harness.Alice.NodeId), introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: the node delta kept
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    #endregion

    #region Expiry bounds (NL-922)

    [Fact]
    public async Task Given_AnIncomingExpiryBeyondMaxCltvExpiryDistance_When_TheSetCompletes_Then_InvalidOnionBlinding()
    {
        // Arrange: the HTLC expires 700 blocks from now, Carol accepts at most 600
        await using var harness = await CreateHarnessAsync(carolAlice: true, routing: r => r.MaxCltvExpiryDistance = 600);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: on 9418c968 the leg started
        await AssertRefusedAtCompletionAsync(harness, payment, onion, "too far");
    }

    [Fact]
    public async Task Given_AnOutgoingExpiryWithinExpiryTooSoonBlocks_When_TheSetCompletes_Then_InvalidOnionBlinding()
    {
        // Arrange: Carol's chain is 640 blocks ahead, so the expiry out (incoming - 50) is 10 blocks away (<= 18) while
        // the incoming one is still 60 blocks away (above MinCltvMarginBlocks)
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);
        _carolMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(ThreeNodeHarness.BlockHeight + 640);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert: on 9418c968 the leg started
        await AssertRefusedAtCompletionAsync(harness, payment, onion, "too soon");
    }

    [Fact]
    public async Task Given_AnHtlcExpiryAboveMaxCltvExpiry_When_ThePartArrives_Then_RefusedBeforeItJoins()
    {
        // Arrange: max_cltv_expiry one block below the HTLC's expiry; the outer expiry (what the processor checks)
        // five blocks below it
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var data = CarolData(ThreeNodeHarness.CarolAliceScid,
                             constraints: new BlindedPaymentConstraints(IncomingCltv - 1, 1));
        var payment = await NewPaymentAsync(harness, data, introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total, outerCltv: IncomingCltv - 5);
        await harness.PumpAsync();

        // Assert
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              $"cltv_expiry {IncomingCltv} is above payment_constraints.max_cltv_expiry");
    }

    [Fact]
    public async Task Given_AnHtlcBelowTheOuterAmtToForward_When_ThePartArrives_Then_RefusedBeforeItJoins()
    {
        // Arrange
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.CarolAliceScid), introduction: true);

        // Act: the outer payload promises one msat more than the HTLC brings
        var onion = await PayPartAsync(harness, payment, s_total,
                                       outerAmount: s_total + LightningMoney.MilliSatoshis(1));
        await harness.PumpAsync();

        // Assert
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              $"amount_msat {s_total.MilliSatoshi} is below the outer amt_to_forward");
    }

    #endregion

    #region Next node, more (NL-922 review)

    [Fact]
    public async Task Given_ARecipientDataNamingARetiredScid_When_TheSetCompletes_Then_TheLegGoesToThatChannelsPeer()
    {
        // Arrange: a splice retired s_retiredScid of the Carol-Alice channel (IRetiredScidMap)
        _retiredScids.Scid = s_retiredScid;
        _retiredScids.Channel = ThreeNodeHarness.CarolAliceChannelId;
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(s_retiredScid), introduction: true);

        // Act
        await PayPartAsync(harness, payment, s_total);
        await harness.PumpAsync();

        // Assert
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.True(_retiredScids.Lookups > 0);
        Assert.Equal(harness.Alice.NodeId, (await GetRelayAsync(harness, payment.Hash)).Relay.NextNodeId);
    }

    [Fact]
    public async Task Given_ACompulsoryScidAliasChannel_When_TheRecipientDataNamesItsRealScid_Then_Refused()
    {
        // Arrange: Bob-Carol requires option_scid_alias, so its real scid names no channel (BOLT 2)
        await using var harness = await CreateHarnessAsync(bobCarolScidAlias: FeatureSupport.Compulsory);
        var payment = await NewPaymentAsync(harness, CarolData(ThreeNodeHarness.BobCarolScid), introduction: true);

        // Act
        var onion = await PayPartAsync(harness, payment, s_total, bobCarolScid: ThreeNodeHarness.BobCarolCarolAlias);
        await harness.PumpAsync();

        // Assert
        await AssertRefusedBeforeJoiningAsync(harness, payment, onion,
                                              $"short_channel_id {ThreeNodeHarness.BobCarolScid}, which is none of our "
                                            + "open channels");
    }

    [Fact]
    public async Task Given_ACrashAfterTheLastPartsSave_When_TheCollectingReplayCompletesTheSet_Then_TheStoredNodeIsUsed()
    {
        // Arrange: the recipient data names a retired scid of the Carol-Alice channel; the first part joins
        _retiredScids.Scid = s_retiredScid;
        _retiredScids.Channel = ThreeNodeHarness.CarolAliceChannelId;
        await using var harness = await CreateHarnessAsync(carolAlice: true);
        var payment = await NewPaymentAsync(harness, CarolData(s_retiredScid), introduction: true);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();
        Assert.Equal(harness.Alice.NodeId, (await GetRelayAsync(harness, payment.Hash)).Relay.NextNodeId);

        // Arrange: the last part locks in while Carol's switch is held, and its row is saved as the engine saves it,
        // then Carol "crashes" before marking the relay Sending
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();
        var last = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                          .Where(h => h is { Direction: HtlcDirection.Incoming, Removal: null })
                          .MaxBy(h => h.Id)!;
        var lastOnion = Assert.IsType<IncomingOnionTrampolineRelay>(
            await harness.Carol.Services.GetRequiredService<IncomingOnionProcessor>()
                         .ProcessAsync(last.OnionRoutingPacket, last.PaymentHash, null, last.PathKey,
                                       LightningMoney.MilliSatoshis(last.AmountMsat), last.CltvExpiry));
        await harness.Carol.InScopeAsync(async u =>
        {
            await u.TrampolineRelayDbRepository.AddPartAsync(new TrampolineRelayPartModel(
                                                                 payment.Hash, ThreeNodeHarness.BobCarolChannelId,
                                                                 last.Id,
                                                                 LightningMoney.MilliSatoshis(last.AmountMsat),
                                                                 last.CltvExpiry, lastOnion.OuterSharedSecret,
                                                                 lastOnion.TrampolineSharedSecret,
                                                                 payment.OuterSecret));
            await u.SaveChangesAsync();
            return true;
        });
        Assert.Empty(_legSender.Started);

        // Arrange: from now on the scid names nothing: resolving it again would refuse the set
        _retiredScids.Resolves = false;
        var lookupsBefore = _retiredScids.Lookups;

        // Act: Carol restarts; the replayed lock-ins find the relay Collecting with every part
        harness.Carol.SwitchSuspended = false;
        await harness.RestartAsync(harness.Carol);
        await harness.Carol.Services.GetRequiredService<TrampolineRelayService>()
                   .StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert: one leg to the stored node with the stored path key, the scid never resolved again, and the delta
        // kept without the parts' memory is Node:Routing's (no channel policy asks more)
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(relay.NextPathKey, (byte[])leg.NextPathKey!.Value);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - 40, leg.MaxFirstHopCltvExpiry);
        Assert.Equal(lookupsBefore, _retiredScids.Lookups);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(2, parts.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_AChannelDeltaBelowTheNodesAndARestart_When_TheCollectingReplayCompletesTheSet_Then_TheKeptDeltaIsRead()
    {
        // Arrange: setchannelpolicy delta 34 on the Carol-Alice channel (Node:Routing: 40), the payment_relay paying
        // 36, so the first part's price check keeps the channel's 34 (NL-922), not the node's 40
        await using var harness = await CreateHarnessWithChannelPoliciesAsync();
        await SetCarolAlicePolicyAsync(harness, cltvExpiryDelta: 34);
        PassThePolicyGracePeriod();
        var payment = await NewPaymentAsync(harness,
                                            CarolData(ThreeNodeHarness.CarolAliceScid, new BlindedPaymentRelay(36, 10, 100)),
                                            introduction: true);
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(400_000));
        await harness.PumpAsync();

        // Arrange: the last part locks in while Carol's switch is held, and its row is saved as the engine saves it,
        // then Carol "crashes" before the set completes (a restart between the last part's save and Sending)
        harness.Carol.SwitchSuspended = true;
        await PayPartAsync(harness, payment, LightningMoney.MilliSatoshis(600_200));
        await harness.PumpAsync();
        var last = harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).Commitments!.Htlcs.Values
                          .Where(h => h is { Direction: HtlcDirection.Incoming, Removal: null })
                          .MaxBy(h => h.Id)!;
        var lastOnion = Assert.IsType<IncomingOnionTrampolineRelay>(
            await harness.Carol.Services.GetRequiredService<IncomingOnionProcessor>()
                         .ProcessAsync(last.OnionRoutingPacket, last.PaymentHash, null, last.PathKey,
                                       LightningMoney.MilliSatoshis(last.AmountMsat), last.CltvExpiry));
        await harness.Carol.InScopeAsync(async u =>
        {
            await u.TrampolineRelayDbRepository.AddPartAsync(new TrampolineRelayPartModel(
                                                                 payment.Hash, ThreeNodeHarness.BobCarolChannelId,
                                                                 last.Id,
                                                                 LightningMoney.MilliSatoshis(last.AmountMsat),
                                                                 last.CltvExpiry, lastOnion.OuterSharedSecret,
                                                                 lastOnion.TrampolineSharedSecret,
                                                                 payment.OuterSecret));
            await u.SaveChangesAsync();
            return true;
        });
        Assert.Empty(_legSender.Started);

        // Act: Carol restarts; the Collecting replay completes the set with only the relay row (no part's memory)
        harness.Carol.SwitchSuspended = false;
        await harness.RestartAsync(harness.Carol);
        await harness.Carol.Services.GetRequiredService<TrampolineRelayService>()
                   .StartAsync(TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Carol);
        await harness.PumpAsync();

        // Assert: the row's kept delta (34) is read, not Node:Routing's 40 (NL-923: on 7be91cb2~ the upper bound 40
        // refused the set whose payment_relay paid 36 with invalid_onion_blinding); the leg may expire as late as the
        // channel allows
        var leg = Assert.Single(_legSender.Started);
        Assert.Equal(harness.Alice.NodeId, leg.NextNodeId);
        Assert.Equal(s_amountOut, leg.Amount);
        Assert.Equal(IncomingCltv - 36, leg.FinalCltvExpiry);
        Assert.Equal(IncomingCltv - 34, leg.MaxFirstHopCltvExpiry);
        var (relay, parts) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Sending, relay.Status);
        Assert.Equal(2, parts.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Carol with the relay engine, the fake leg, her log recorder and the switchable retired-scid map; her
    /// <c>Node:Routing</c> is 100 msat + 10 ppm, delta 40 (what <see cref="s_carolRelay"/> pays exactly), changed by
    /// <paramref name="routing"/>; <paramref name="services"/> adds to her services (e.g. the channel policies).
    /// </summary>
    private Task<ThreeNodeHarness> CreateHarnessAsync(bool carolAlice = false,
                                                      FeatureSupport bobCarolScidAlias = FeatureSupport.No,
                                                      Action<RoutingOptions>? routing = null,
                                                      Action<IServiceCollection>? services = null) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Carol.Options.Features.OptionTrampolineRouting = FeatureSupport.Optional;
            h.Carol.Options.Features.AllowExperimentalFeatures = true;
            h.Carol.Options.Routing.FeeBaseMsat = s_carolRelay.FeeBaseMsat;
            h.Carol.Options.Routing.FeeProportionalMillionths = s_carolRelay.FeeProportionalMillionths;
            routing?.Invoke(h.Carol.Options.Routing);
            h.Carol.ConfigureServices = carolServices =>
            {
                carolServices.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
                carolServices.Replace(ServiceDescriptor.Singleton(_carolMonitor.Object));
                carolServices.Replace(ServiceDescriptor.Singleton<IRetiredScidMap>(_retiredScids));
                carolServices.AddSingleton<ILoggerProvider>(_carolLogs);
                carolServices.Configure<HtlcSwitchOptions>(o => o.BlindedErrorMaxDelay = TimeSpan.Zero);
                carolServices.AddTrampolineRelayServices();
                carolServices.AddSingleton<ITrampolineLegSender>(_legSender);
                services?.Invoke(carolServices);
            };
        }, bobCarolScidAlias, carolAlice);

    /// <summary>Carol with the per-channel routing policies (<c>setchannelpolicy</c>) on her SQLite database.</summary>
    private Task<ThreeNodeHarness> CreateHarnessWithChannelPoliciesAsync() =>
        CreateHarnessAsync(carolAlice: true, services: s => s.AddChannelPolicyServices());

    /// <summary>Carol's override of the Carol-Alice channel's policy, as <c>setchannelpolicy</c> stores it.</summary>
    private static Task SetCarolAlicePolicyAsync(ThreeNodeHarness harness, uint? feeBaseMsat = null,
                                                 ushort? cltvExpiryDelta = null) =>
        harness.Carol.Services.GetRequiredService<ChannelPolicyStore>()
               .SaveAsync(new ChannelPolicyOverride(ThreeNodeHarness.CarolAliceChannelId, FeeBaseMsat: feeBaseMsat,
                                                    CltvExpiryDelta: cltvExpiryDelta),
                          TestContext.Current.CancellationToken);

    /// <summary>Past the BOLT 7 grace period of a replaced policy.</summary>
    private void PassThePolicyGracePeriod() =>
        _clock.Advance(ChannelPolicyStore.PreviousPolicyGracePeriod + TimeSpan.FromMinutes(1));

    /// <summary>
    /// Carol's own part refused before it joins a relay: Alice reads our own <c>invalid_onion_blinding</c>, nothing
    /// is stored, no leg starts, and the log names <paramref name="reason"/>.
    /// </summary>
    private async Task AssertRefusedBeforeJoiningAsync(ThreeNodeHarness harness, BlindedPayment payment,
                                                       PaymentOnion onion, string reason)
    {
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        Assert.Null(await harness.Carol.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(payment.Hash)));
        AssertPartRefused(reason);
    }

    /// <summary>
    /// The relay refused at its completion: Alice reads our own <c>invalid_onion_blinding</c> (never NODE|26), the
    /// relay is Failed with that code and the reason, and no leg started.
    /// </summary>
    private async Task AssertRefusedAtCompletionAsync(ThreeNodeHarness harness, BlindedPayment payment,
                                                      PaymentOnion onion, string reason)
    {
        var decrypted = Decrypt(harness, onion, payment.Trampoline);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(FailureCode.InvalidOnionBlinding, decrypted.Code);
        Assert.Empty(_legSender.Started);
        var (relay, _) = await GetRelayAsync(harness, payment.Hash);
        Assert.Equal(TrampolineRelayStatus.Failed, relay.Status);
        Assert.Equal((ushort)FailureCode.InvalidOnionBlinding, relay.FailureCode);
        Assert.Contains(reason, relay.FailureReason);
    }

    private static BlindedRecipientData CarolData(ShortChannelId shortChannelId, BlindedPaymentRelay? relay = null,
                                                  BlindedPaymentConstraints? constraints = null)
    {
        return new BlindedRecipientData
        {
            ShortChannelId = shortChannelId,
            PaymentRelay = relay ?? s_carolRelay,
            PaymentConstraints = constraints ?? new BlindedPaymentConstraints(IncomingCltv + 10_000, 1)
        };
    }

    private static BlindedRecipientData CarolDataToNode(CompactPubKey nextNodeId, BlindedPaymentRelay? relay = null) =>
        new()
        {
            NextNodeId = nextNodeId,
            PaymentRelay = relay ?? s_carolRelay,
            PaymentConstraints = new BlindedPaymentConstraints(IncomingCltv + 10_000, 1)
        };

    /// <summary>
    /// Alice's blinded path (Carol → Alice, or Bob → Carol → Alice when Carol is not the introduction node) with Carol's
    /// <paramref name="carolData"/>, its hops as trampoline hops (BOLTs PR 836 TR-R-07), and, past the introduction
    /// node, Carol's path key for the outer payload (what Bob, the previous trampoline hop, would send her).
    /// </summary>
    private static async Task<BlindedPayment> NewPaymentAsync(ThreeNodeHarness harness, BlindedRecipientData carolData,
                                                              bool introduction)
    {
        var preimage = new Secret(RandomNumberGenerator.GetBytes(32));
        var hash = new Hash(SHA256.HashData((byte[])preimage));
        var blinding = harness.Alice.Services.GetRequiredService<IRouteBlindingService>();
        var nodeIds = new List<CompactPubKey>();
        var data = new List<BlindedRecipientData>();
        if (!introduction)
        {
            nodeIds.Add(harness.Bob.NodeId);
            data.Add(new BlindedRecipientData
            {
                NextNodeId = harness.Carol.NodeId,
                PaymentRelay = s_carolRelay,
                PaymentConstraints = new BlindedPaymentConstraints(IncomingCltv + 20_000, 1)
            });
        }

        nodeIds.Add(harness.Carol.NodeId);
        data.Add(carolData);
        nodeIds.Add(harness.Alice.NodeId);
        data.Add(new BlindedRecipientData { PathId = RandomNumberGenerator.GetBytes(32) });
        var path = blinding.CreateBlindedPath(nodeIds, data.Select(blinding.EncodeRecipientData).ToList(),
                                              RandomNumberGenerator.GetBytes(32));

        var carolIndex = introduction ? 0 : 1;
        var carolHop = path.Hops[carolIndex];
        CompactPubKey? outerPathKey = null;
        var hops = new List<(CompactPubKey, HopPayload)>();
        if (introduction)
        {
            hops.Add((harness.Carol.NodeId,
                      new HopPayload(new EncryptedRecipientDataTlv(carolHop.EncryptedRecipientData.Span),
                                     new CurrentPathKeyTlv(path.FirstPathKey))));
        }
        else
        {
            outerPathKey = harness.Bob.Services.GetRequiredService<IRouteBlindingService>()
                                  .UnblindAsLocalNode(path.FirstPathKey, path.Hops[0].EncryptedRecipientData)
                                  .NextPathKey;
            hops.Add((carolHop.BlindedNodeId,
                      new HopPayload(new EncryptedRecipientDataTlv(carolHop.EncryptedRecipientData.Span))));
        }

        hops.Add((path.Hops[^1].BlindedNodeId,
                  new HopPayload(new AmtToForwardTlv(s_amountOut), new OutgoingCltvValueTlv(FinalCltv),
                                 new EncryptedRecipientDataTlv(path.Hops[^1].EncryptedRecipientData.Span),
                                 new TotalAmountMsatTlv(s_amountOut))));

        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
        {
            using var stream = new MemoryStream();
            await serializer.SerializeAsync(payload, stream);
            onionHops.Add(new OnionHop(nodeId, stream.ToArray()));
        }

        var trampoline = harness.Alice.Services.GetRequiredService<ITrampolineOnionService>()
                                .Build(onionHops, RandomNumberGenerator.GetBytes(32), hash,
                                       TrampolineOnionSizePolicy.Auto(650));
        return new BlindedPayment(hash, trampoline, RandomNumberGenerator.GetBytes(32), outerPathKey);
    }

    /// <summary>
    /// Alice → Bob → Carol, one HTLC of <paramref name="part"/> expiring at <see cref="IncomingCltv"/>, whose outer
    /// final payload promises <see cref="s_total"/> and carries the trampoline onion (and, past the introduction
    /// node, Carol's path key).
    /// </summary>
    private static async Task<PaymentOnion> PayPartAsync(ThreeNodeHarness harness, BlindedPayment payment,
                                                         LightningMoney part, uint? outerCltv = null,
                                                         ShortChannelId? bobCarolScid = null,
                                                         LightningMoney? outerAmount = null)
    {
        var route = harness.RouteToCarol(part, payment.Hash, new Secret(payment.OuterSecret), IncomingCltvDelta,
                                         bobCarolScid: bobCarolScid);
        var serializer = harness.Alice.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            HopPayload payload;
            if (hop.IsFinal)
            {
                var tlvs = new List<Domain.Protocol.Tlv.BaseTlv>
                {
                    new AmtToForwardTlv(outerAmount ?? hop.AmountToForward),
                    new OutgoingCltvValueTlv(outerCltv ?? hop.OutgoingCltvValue),
                    new PaymentDataTlv(payment.OuterSecret, s_total),
                    new TrampolineOnionPacketTlv(payment.Trampoline.Packet)
                };
                if (payment.OuterPathKey is { } pathKey)
                    tlvs.Add(new CurrentPathKeyTlv(pathKey));
                payload = new HopPayload(tlvs.ToArray());
            }
            else
            {
                payload = PaymentOnionFactory.CreatePayload(hop, route);
            }

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

    private static TrampolineDecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion,
                                                      TrampolineOnion trampoline)
    {
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<ITrampolineFailureOnionService>()
                               .DecryptTrampolineErrorPacket(onion.SharedSecrets, trampoline.SharedSecrets,
                                                             failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }

    private sealed record BlindedPayment(Hash Hash, TrampolineOnion Trampoline, byte[] OuterSecret,
                                         CompactPubKey? OuterPathKey);

    /// <summary>Carol's part refusal was logged with <paramref name="reason"/> (what only that check says).</summary>
    private void AssertPartRefused(string reason) =>
        Assert.Contains(_carolLogs.Messages, m => m.Contains("Blinded trampoline part") && m.Contains(reason));

    /// <summary>Records the relay engine's log lines.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public ILogger CreateLogger(string categoryName) =>
            categoryName.EndsWith(nameof(TrampolineRelayService), StringComparison.Ordinal)
                ? new RecordingLogger(_messages)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>A retired-scid map with one entry that a test can take away, counting the lookups of it.</summary>
    private sealed class SwitchableRetiredScidMap : IRetiredScidMap
    {
        private int _lookups;

        public ShortChannelId? Scid { get; set; }
        public ChannelId Channel { get; set; }
        public bool Resolves { get; set; } = true;
        public int Lookups => Volatile.Read(ref _lookups);

        public void Retire(RetiredShortChannelId retired)
        {
        }

        public bool TryResolve(ShortChannelId shortChannelId, out ChannelId channelId)
        {
            channelId = default;
            if (shortChannelId != Scid)
                return false;

            Interlocked.Increment(ref _lookups);
            if (!Resolves)
                return false;

            channelId = Channel;
            return true;
        }

        public IReadOnlyList<RetiredShortChannelId> GetByChannel(ChannelId channelId) => [];

        public int PruneExpired(uint height) => 0;

        public Task LoadAsync(uint currentHeight, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Records the legs the engine starts.</summary>
    private sealed class FakeLegSender : ITrampolineLegSender
    {
        private readonly List<TrampolineLegRequest> _started = [];

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
            return Task.CompletedTask;
        }

        public Task HandleOutgoingFulfilledAsync(OutgoingHtlcFulfilled fulfilled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task HandleOutgoingFailedAsync(OutgoingHtlcFailed failed, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    #endregion
}