using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Payloads;
using Harness;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// BOLT2 plan N10 proof in process: two real channel managers close a channel cooperatively (shutdown both ways,
/// HTLCs cleared first, legacy closing_signed with and without fee_range), and both end with the same fully signed
/// closing transaction, which NBitcoin's interpreter accepts against the funding output. Also the close across a
/// reconnection (B2-RE-28 shutdown re-send, B2-RE-29 negotiation restart) and the add/update refusals after shutdown.
/// </summary>
public class CooperativeCloseHarnessTests
{
    private const uint CltvExpiry = 700;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    private const long AliceSat = (long)(TwoNodeHarness.FundingSatoshis - TwoNodeHarness.PushSatoshis);
    private const long BobSat = (long)TwoNodeHarness.PushSatoshis;

    [Fact]
    public async Task Given_OpenChannel_When_FunderCloses_Then_BothBroadcastTheSameClosingTransaction()
    {
        // Arrange
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var started = await close.CloseService(close.Alice)
                                 .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(), ct);
        await close.Harness.PumpAsync();

        // Assert
        Assert.Equal(ChannelState.ShuttingDown, started.State);
        var tx = close.AssertClosedTogether();
        var fee = TwoNodeHarness.FundingSatoshis - (ulong)tx.Outputs.Sum(o => o.Value.Satoshi);
        // B2-CLS-02: the funder's estimate at 2500 sat/kw for two P2WPKH outputs (weight 676)
        Assert.Equal(ClosingFeeCalculator.FeeSat(2_500, 676), fee);
        Assert.Equal(AliceSat - (long)fee, CloseHarness.OutputTo(tx, CloseHarness.AliceScript));
        Assert.Equal(BobSat, CloseHarness.OutputTo(tx, CloseHarness.BobScript));
        Assert.Equal(2U, tx.Version);
        Assert.Equal(0U, (uint)tx.LockTime);

        // Wire: Bob got shutdown then closing_signed with fee_range; the negotiation ended after Bob's echo
        var aliceSigned = close.Bob.Received.OfType<ClosingSignedMessage>().ToList();
        Assert.IsType<ShutdownMessage>(close.Bob.Received[0]);
        Assert.Equal(CloseHarness.AliceScript, ((ShutdownMessage)close.Bob.Received[0]).Payload.ScriptPubkey);
        Assert.Single(aliceSigned);
        Assert.NotNull(aliceSigned[0].FeeRangeTlv);
        var bobSigned = Assert.Single(close.Alice.Received.OfType<ClosingSignedMessage>());
        Assert.Equal(aliceSigned[0].Payload.FeeAmount, bobSigned.Payload.FeeAmount);
    }

    [Fact]
    public async Task Given_OpenChannel_When_NonFunderCloses_Then_FunderRepliesAndProposes()
    {
        // Arrange
        using var close = new CloseHarness();

        // Act
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              TestContext.Current.CancellationToken);
        await close.Harness.PumpAsync();

        // Assert: Alice replied with her shutdown (B2-SHUT-R04) and, as the funder, sent the first closing_signed
        var tx = close.AssertClosedTogether();
        Assert.IsType<ShutdownMessage>(close.Alice.Received[0]);
        Assert.IsType<ShutdownMessage>(close.Bob.Received[0]);
        Assert.IsType<ClosingSignedMessage>(close.Bob.Received[1]);
        Assert.Equal(BobSat, CloseHarness.OutputTo(tx, CloseHarness.BobScript));
    }

    [Fact]
    public async Task Given_NoFeeRange_When_Close_Then_NegotiatedWithoutFeeRange()
    {
        // Arrange: neither side sends fee_range (legacy "strictly between" negotiation)
        using var close = new CloseHarness(aliceFeeratePerKw: 1_000, bobFeeratePerKw: 20_000,
                                           aliceSendsFeeRange: false, bobSendsFeeRange: false);

        // Act
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                                new ChannelCloseRequest(SendFeeRange: false),
                                                                TestContext.Current.CancellationToken);
        await close.Harness.PumpAsync();

        // Assert
        var tx = close.AssertClosedTogether();
        Assert.All(close.Bob.Received.OfType<ClosingSignedMessage>(), m => Assert.Null(m.FeeRangeTlv));
        Assert.All(close.Alice.Received.OfType<ClosingSignedMessage>(), m => Assert.Null(m.FeeRangeTlv));
        var fee = TwoNodeHarness.FundingSatoshis - (ulong)tx.Outputs.Sum(o => o.Value.Satoshi);
        Assert.Equal(ClosingFeeCalculator.FeeSat(1_000, 676), fee);
    }

    [Fact]
    public async Task Given_HtlcInFlight_When_Close_Then_NoNewAddAndNegotiationWaitsUntilItSettles()
    {
        // Arrange: an HTLC from Alice to Bob is locked in
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        var preimage = TwoNodeHarness.Preimage(7);
        var htlcId = await OfferAsync(close.Alice, 50_000_000, preimage);
        await close.Harness.PumpAsync();

        // Act 1: Bob closes
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              ct);
        await close.Harness.PumpAsync();

        // Assert 1: both shut down, nothing negotiated while the HTLC is there (B2-CLS-01), no new add (B2-ADD-S13)
        Assert.Equal(ChannelState.ShuttingDown, close.Alice.Channel.State);
        Assert.Equal(ChannelState.ShuttingDown, close.Bob.Channel.State);
        Assert.Empty(close.Bob.Received.OfType<ClosingSignedMessage>());
        var refused = await Assert.ThrowsAsync<CommitmentRefusedException>(
                          () => OfferAsync(close.Alice, 10_000_000, TwoNodeHarness.Preimage(8)));
        Assert.Equal("B2-ADD-S13", refused.RequirementId);
        // B2-SHUT-S07 does not apply yet: an HTLC is left, so the funder may still change the fee
        await close.Alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, 3_000, ct);
        await close.Harness.PumpAsync();

        // Act 2: Bob settles it
        await close.Bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, htlcId, preimage, ct);
        await close.Harness.PumpAsync();

        // Assert 2: the settled amount is Bob's in the closing transaction; the fee uses Alice's estimate
        var tx = close.AssertClosedTogether();
        Assert.Equal(BobSat + 50_000, CloseHarness.OutputTo(tx, CloseHarness.BobScript));
        Assert.Equal(TwoNodeHarness.FundingSatoshis - ClosingFeeCalculator.FeeSat(2_500, 676),
                     (ulong)tx.Outputs.Sum(o => o.Value.Satoshi));
    }

    [Fact]
    public async Task Given_ShuttingDownWithHtlc_When_Fulfilled_Then_ShutdownNeverFollowsUnsignedUpdate()
    {
        // Arrange: an HTLC from Alice to Bob; Bob fulfills it and closes right away
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        var preimage = TwoNodeHarness.Preimage(9);
        var htlcId = await OfferAsync(close.Alice, 20_000_000, preimage);
        await close.Harness.PumpAsync();

        // Act
        await close.Bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, htlcId, preimage, ct);
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              ct);
        await close.Harness.PumpAsync();

        // Assert: B2-SHUT-S03, a commitment_signed covering the fulfill precedes Bob's shutdown on the wire
        close.AssertClosedTogether();
        var received = close.Alice.Received;
        var fulfill = received.FindIndex(m => m is UpdateFulfillHtlcMessage);
        var shutdown = received.FindIndex(m => m is ShutdownMessage);
        var commit = received.FindIndex(fulfill, m => m is CommitmentSignedMessage);
        Assert.InRange(commit, fulfill + 1, shutdown - 1);
    }

    [Fact]
    public async Task Given_ShutdownSent_When_Reconnect_Then_ShutdownResentAndCloseCompletes()
    {
        // Arrange: an HTLC keeps the channel in ShuttingDown across the reconnection
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        var preimage = TwoNodeHarness.Preimage(3);
        var htlcId = await OfferAsync(close.Alice, 30_000_000, preimage);
        await close.Harness.PumpAsync();
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                ct);
        await close.Harness.PumpAsync();
        var bobBefore = close.Bob.Received.Count;
        var aliceBefore = close.Alice.Received.Count;

        // Act: the link drops and comes back
        await close.Harness.DisconnectAsync();
        await close.Harness.ReconnectAsync();
        await close.Harness.PumpAsync();

        // Assert (B2-RE-28): each side re-sent its shutdown right after the reestablish exchange
        var bobAfter = close.Bob.Received.Skip(bobBefore).ToList();
        var aliceAfter = close.Alice.Received.Skip(aliceBefore).ToList();
        Assert.IsType<ChannelReestablishMessage>(bobAfter[0]);
        Assert.Contains(bobAfter, m => m is ShutdownMessage);
        Assert.IsType<ChannelReestablishMessage>(aliceAfter[0]);
        Assert.Contains(aliceAfter, m => m is ShutdownMessage);
        Assert.Equal(ChannelState.ShuttingDown, close.Alice.Channel.State);

        // Act 2: the HTLC settles, then the close completes
        await close.Bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, htlcId, preimage, ct);
        await close.Harness.PumpAsync();
        var tx = close.AssertClosedTogether();
        Assert.Equal(BobSat + 30_000, CloseHarness.OutputTo(tx, CloseHarness.BobScript));
    }

    [Fact]
    public async Task Given_Negotiating_When_Reconnect_Then_NegotiationRestartsAndCompletes()
    {
        // Arrange: stop right after both shutdowns: Alice's first closing_signed is still queued
        using var close = new CloseHarness();
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                TestContext.Current.CancellationToken);
        close.Harness.DeliveryBudget = 2;
        await close.Harness.PumpAsync();
        Assert.Equal(ChannelState.Negotiating, close.Alice.Channel.State);
        Assert.Equal(ChannelState.Negotiating, close.Bob.Channel.State);

        // Act: the link drops (the closing_signed is lost), then comes back
        await close.Harness.DisconnectAsync();
        Assert.Contains(close.Alice.Lost, m => m is ClosingSignedMessage);
        close.Harness.DeliveryBudget = null;
        await close.Harness.ReconnectAsync();
        await close.Harness.PumpAsync();

        // Assert (B2-RE-29): the negotiation started over on the new connection and finished
        close.AssertClosedTogether();
        Assert.Single(close.Bob.Received.OfType<ClosingSignedMessage>());
    }

    [Fact]
    public async Task Given_Closing_When_CloseAgain_Then_ReportsTheClosingTransaction()
    {
        // Arrange
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                ct);
        await close.Harness.PumpAsync();

        // Act
        var again = await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                                            new ChannelCloseRequest(), ct);

        // Assert
        Assert.Equal(ChannelState.Closing, again.State);
        Assert.Equal(close.Alice.Channel.ClosingTransaction!.TxId, again.ClosingTxId);
    }

    [Fact]
    public async Task Given_PeerAway_When_Close_Then_Refused()
    {
        // Arrange
        using var close = new CloseHarness();
        close.Alice.PeerAlive = false;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                                    new ChannelCloseRequest(),
                                                                    TestContext.Current.CancellationToken));
        Assert.Equal(ChannelState.Open, close.Alice.Channel.State);
        Assert.Null(close.Alice.Channel.LocalShutdownScript);
    }

    [Fact]
    public async Task Given_UnknownChannel_When_Close_Then_KeyNotFound()
    {
        // Arrange
        using var close = new CloseHarness();

        // Act / Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => close.CloseService(close.Alice).CloseChannelAsync(new ChannelId(new byte[32]),
                                                                    new ChannelCloseRequest(),
                                                                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_RemoteShutdown_When_PeerAddsHtlc_Then_WarningAndDisconnect()
    {
        // Arrange: an HTLC keeps the channel ShuttingDown; Bob sent shutdown; a well-formed add from Bob afterwards
        // breaks B2-SHUT-R06
        using var close = new CloseHarness();
        await OfferAsync(close.Alice, 10_000_000, TwoNodeHarness.Preimage(5));
        await close.Harness.PumpAsync();
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              TestContext.Current.CancellationToken);
        await close.Bob.DeliverNextAsync();
        var htlcsBefore = close.Alice.State.Htlcs.Count;
        var add = new UpdateAddHtlcMessage(new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(10_000_000),
                                                                    TwoNodeHarness.ChannelId,
                                                                    CltvExpiry, 0,
                                                                    TwoNodeHarness.Hash(TwoNodeHarness.Preimage(1)),
                                                                    TwoNodeHarness.Onion));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Alice.ChannelManager.HandleChannelMessageAsync(
                              add, new FeatureOptions(), close.Bob.NodeId));

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Contains("B2-SHUT-R06", warning.Message);
        Assert.Equal(htlcsBefore, close.Alice.State.Htlcs.Count);
        Assert.Equal(ChannelState.ShuttingDown, close.Alice.Channel.State);
    }

    [Fact]
    public async Task Given_ShuttingDownWithoutHtlc_When_UpdateFee_Then_Refused()
    {
        // Arrange: Alice sent shutdown, Bob's is not delivered yet, no HTLC left (B2-SHUT-S07)
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                ct);

        // Act / Assert
        var refused = await Assert.ThrowsAsync<CommitmentRefusedException>(
                          () => close.Alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, 3_000, ct));
        Assert.Equal("B2-SHUT-S07", refused.RequirementId);
    }

    [Theory]
    [InlineData("76a914aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa88ac", false, false)]
    [InlineData("5120eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", false, false)]
    [InlineData("5120eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", true, true)]
    [InlineData("0020aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, true)]
    public async Task Given_ShutdownScript_When_Received_Then_OnlyBolt2FormsAccepted(string scriptHex,
                                                                                     bool anySegwit, bool accepted)
    {
        // Arrange (B2-SHUT-R02: a script of another form gets a warning and changes nothing)
        using var close = new CloseHarness();
        var shutdown = new ShutdownMessage(new ShutdownPayload(TwoNodeHarness.ChannelId,
                                                               Convert.FromHexString(scriptHex)));
        var features = new FeatureOptions
        {
            BeyondSegwitShutdown = anySegwit ? FeatureSupport.Optional : FeatureSupport.No
        };

        // Act
        var exception = await Record.ExceptionAsync(
                            () => close.Alice.ChannelManager.HandleChannelMessageAsync(shutdown, features,
                                                                                       close.Bob.NodeId));

        // Assert
        if (accepted)
        {
            Assert.Null(exception);
            Assert.Equal(ChannelState.Negotiating, close.Alice.Channel.State);
            Assert.Equal((BitcoinScript)Convert.FromHexString(scriptHex), close.Alice.Channel.RemoteShutdownScript);
        }
        else
        {
            var warning = Assert.IsType<ChannelWarningException>(exception);
            Assert.Contains("B2-SHUT-R02", warning.Message);
            Assert.Equal(ChannelState.Open, close.Alice.Channel.State);
        }
    }

    [Fact]
    public async Task Given_SecondShutdownWithOtherScript_When_Received_Then_WarningAndDisconnect()
    {
        // Arrange
        using var close = new CloseHarness();
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              TestContext.Current.CancellationToken);
        await close.Bob.DeliverNextAsync();
        var other = new ShutdownMessage(new ShutdownPayload(TwoNodeHarness.ChannelId,
                                                            Convert.FromHexString("0014" + new string('c', 40))));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Alice.ChannelManager.HandleChannelMessageAsync(other, new FeatureOptions(),
                                                                                     close.Bob.NodeId));

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Equal(CloseHarness.BobScript, close.Alice.Channel.RemoteShutdownScript);
    }

    [Fact]
    public async Task Given_ClosingSignedWithBadSignature_When_Received_Then_WarningAndNothingBroadcast()
    {
        // Arrange: both shutdowns exchanged, Alice's first closing_signed is still queued
        using var close = new CloseHarness();
        await close.CloseService(close.Alice).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                                TestContext.Current.CancellationToken);
        close.Harness.DeliveryBudget = 2;
        await close.Harness.PumpAsync();
        var forged = new ClosingSignedMessage(new ClosingSignedPayload(TwoNodeHarness.ChannelId,
                                                                       LightningMoney.Satoshis(1_000),
                                                                       new CompactSignature(new byte[64])));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(forged, new FeatureOptions(),
                                                                                   close.Alice.NodeId));

        // Assert (B2-CLS-R01)
        Assert.True(warning.CloseConnection);
        Assert.Contains("B2-CLS-R01", warning.Message);
        Assert.Equal(ChannelState.Negotiating, close.Bob.Channel.State);
        Assert.Empty(close.Published(close.Bob));
    }

    [Fact]
    public async Task Given_ValidVariantWithoutPeerOutput_When_ClosingSigned_Then_Accepted()
    {
        // Arrange (B2-CLS-R01: "valid for either variant"): Alice signs the variant without her own output
        using var close = new CloseHarness();
        await close.CloseService(close.Bob).CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                                              TestContext.Current.CancellationToken);
        close.Harness.DeliveryBudget = 2;
        await close.Harness.PumpAsync();
        close.Alice.DropOutbox();
        var aliceChannel = close.Alice.Channel;
        var builder = close.Alice.Services.GetRequiredService<IClosingTransactionBuilder>();
        var model = LegacyClosingTransactionFactory.Create(aliceChannel.FundingOutput!,
                                                           close.Alice.State.LocalBalanceMsat,
                                                           close.Alice.State.RemoteBalanceMsat, true, 1_500,
                                                           CloseHarness.AliceScript, CloseHarness.BobScript, 546,
                                                           ClosingVariant.WithoutLocalOutput);
        var unsigned = builder.Build(model);
        var signature = close.Alice.Signer.SignChannelTransaction(TwoNodeHarness.ChannelId, unsigned);
        var message = new ClosingSignedMessage(new ClosingSignedPayload(TwoNodeHarness.ChannelId,
                                                                        LightningMoney.Satoshis(1_500), signature));

        // Act: Bob (non-funder, no range) accepts any fee from the relay floor up
        await close.Bob.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(), close.Alice.NodeId);

        // Assert: Bob closes with Alice's variant: only his own output
        Assert.Equal(ChannelState.Closing, close.Bob.Channel.State);
        var tx = Transaction.Load(close.Bob.Channel.ClosingTransaction!.RawTxBytes, Network.RegTest);
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(BobSat, output.Value.Satoshi);
        Assert.True(tx.Inputs.AsIndexedInputs().First().VerifyScript(close.FundingTxOut(), out _));
    }

    [Fact]
    public async Task Given_ClosingSignedBeforeShutdown_When_Received_Then_WarningAndDisconnect()
    {
        // Arrange
        using var close = new CloseHarness();
        var message = new ClosingSignedMessage(new ClosingSignedPayload(TwoNodeHarness.ChannelId,
                                                                        LightningMoney.Satoshis(1_000),
                                                                        new CompactSignature(new byte[64])));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Alice.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                                     close.Bob.NodeId));

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Equal(ChannelState.Open, close.Alice.Channel.State);
    }

    private static Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, Secret preimage)
    {
        var hash = TwoNodeHarness.Hash(preimage);
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }
}