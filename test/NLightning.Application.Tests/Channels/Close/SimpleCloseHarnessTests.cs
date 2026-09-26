using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close.Simple;
using Application.Channels.Managers;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Harness;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// BOLT2 plan N11 (<c>option_simple_close</c>) in process: two real channel managers with the feature negotiated close
/// a channel with <c>closing_complete</c>/<c>closing_sig</c> (each side proposes and pays for its own transaction, the
/// other signs it), bump the fee with a new <c>closing_complete</c>, keep the close across a reconnection, and apply
/// the closee (B2-SC-E*) and <c>closing_sig</c> receiver (B2-SC-G*) rules to crafted messages signed with the peer's
/// real funding key. Every transaction is executed by NBitcoin's interpreter against the funding output.
/// </summary>
public class SimpleCloseHarnessTests
{
    private const long AliceSat = (long)(TwoNodeHarness.FundingSatoshis - TwoNodeHarness.PushSatoshis);
    private const long BobSat = (long)TwoNodeHarness.PushSatoshis;
    private const ulong AliceMsat = (ulong)AliceSat * 1000;
    private const ulong BobMsat = (ulong)BobSat * 1000;

    /// <summary>Two P2WPKH outputs at 2500 sat/kw (the harness estimate): weight 676.</summary>
    private static readonly ulong s_defaultFee = ClosingFeeCalculator.FeeSat(2_500, 676);

    private static readonly BitcoinScript s_aliceNewScript = new([0x00, 0x14, .. Enumerable.Repeat((byte)0xA2, 20)]);

    #region Both sides close

    [Fact]
    public async Task Given_SimpleClose_When_FunderCloses_Then_EachSideProposesAndSignsThePeersTransaction()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);

        // Act
        await CloseAsync(close);

        // Assert: B2-SC-01, both sides proposed; no legacy closing_signed at all
        Assert.Equal(ChannelState.Closing, close.Alice.Channel.State);
        Assert.Equal(ChannelState.Closing, close.Bob.Channel.State);
        Assert.Empty(close.Alice.Received.OfType<ClosingSignedMessage>());
        Assert.Empty(close.Bob.Received.OfType<ClosingSignedMessage>());

        var aliceComplete = Assert.Single(close.Bob.Received.OfType<ClosingCompleteMessage>());
        var bobComplete = Assert.Single(close.Alice.Received.OfType<ClosingCompleteMessage>());
        Assert.Single(close.Alice.Received.OfType<ClosingSigMessage>());
        Assert.Single(close.Bob.Received.OfType<ClosingSigMessage>());

        // B2-SC-C03..C05: scripts and lock time; B2-SC-C06/C07: Alice has more, Bob less
        Assert.Equal(CloseHarness.AliceScript, aliceComplete.Payload.CloserScriptPubKey);
        Assert.Equal(CloseHarness.BobScript, aliceComplete.Payload.CloseeScriptPubKey);
        Assert.Equal(TwoNodeHarness.BlockHeight, aliceComplete.Payload.LockTime);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs],
                     aliceComplete.Signatures.Kinds);
        Assert.Equal([ClosingSigKind.CloserAndCloseeOutputs], bobComplete.Signatures.Kinds);
        Assert.Equal(s_defaultFee, (ulong)aliceComplete.Payload.FeeSatoshis.Satoshi);
        Assert.Equal(s_defaultFee, (ulong)bobComplete.Payload.FeeSatoshis.Satoshi);

        // Each node broadcast both transactions: its own (after closing_sig) and the peer's (after signing it)
        var alicePublished = close.Published(close.Alice).Select(t => t.TxId).ToHashSet();
        var bobPublished = close.Published(close.Bob).Select(t => t.TxId).ToHashSet();
        Assert.Equal(2, alicePublished.Count);
        Assert.Equal(alicePublished, bobPublished);

        // B2-SC-C08 / B3-CLTX-01: the closer pays the fee; version 2, sequence 0xFFFFFFFD, lock time from the message
        var txs = close.Published(close.Alice).Select(t => AssertSimpleClose(close, t, TwoNodeHarness.BlockHeight))
                       .ToList();
        Assert.Contains(txs, tx => CloseHarness.OutputTo(tx, CloseHarness.AliceScript) == AliceSat - (long)s_defaultFee
                                && CloseHarness.OutputTo(tx, CloseHarness.BobScript) == BobSat);
        Assert.Contains(txs, tx => CloseHarness.OutputTo(tx, CloseHarness.AliceScript) == AliceSat
                                && CloseHarness.OutputTo(tx, CloseHarness.BobScript) == BobSat - (long)s_defaultFee);
        Assert.Contains(close.Alice.Channel.ClosingTransaction!.TxId, alicePublished);
        Assert.Contains(close.Bob.Channel.ClosingTransaction!.TxId, bobPublished);
    }

    [Fact]
    public async Task Given_LesserSideCannotKeepItsOutput_When_Close_Then_ClosesWithCloseeOutputOnly()
    {
        // Arrange: Bob (the lesser side) wants a fee higher than his balance: his own output would be dust
        using var close = new CloseHarness(bobFeeratePerKw: 5_000_000, simpleClose: true);

        // Act
        await CloseAsync(close, close.Bob);

        // Assert: B2-SC-C06 (lesser, own output dust -> closee_output_only only), B2-SC-E06 (Alice uses it)
        var bobComplete = Assert.Single(close.Alice.Received.OfType<ClosingCompleteMessage>());
        Assert.Equal([ClosingSigKind.CloseeOutputOnly], bobComplete.Signatures.Kinds);
        Assert.Equal((ulong)BobSat, (ulong)bobComplete.Payload.FeeSatoshis.Satoshi);
        var aliceSig = Assert.Single(close.Bob.Received.OfType<ClosingSigMessage>());
        Assert.Equal([ClosingSigKind.CloseeOutputOnly], aliceSig.Signatures.Kinds);

        var bobTx = close.Published(close.Bob).Select(t => AssertSimpleClose(close, t, TwoNodeHarness.BlockHeight))
                         .Single(tx => tx.Outputs.Count == 1);
        Assert.Equal(AliceSat, CloseHarness.OutputTo(bobTx, CloseHarness.AliceScript));
    }

    [Fact]
    public async Task Given_HigherSideWithHugeFeerate_When_Close_Then_FeeLeavesItsOutputAtDust()
    {
        // Arrange: Alice (more funds) asks for a fee above her balance
        using var close = new CloseHarness(aliceFeeratePerKw: 5_000_000, simpleClose: true);

        // Act
        await CloseAsync(close);

        // Assert: B2-SC-C01/C07: the fee is capped so her output stays at the P2WPKH dust threshold, and both
        // closer_output_only and closer_and_closee_outputs are sent (Bob's output is not dust)
        var aliceComplete = Assert.Single(close.Bob.Received.OfType<ClosingCompleteMessage>());
        Assert.Equal((ulong)AliceSat - ShutdownScriptValidator.P2WpkhDustSat,
                     (ulong)aliceComplete.Payload.FeeSatoshis.Satoshi);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs],
                     aliceComplete.Signatures.Kinds);
        var bobSig = Assert.Single(close.Alice.Received.OfType<ClosingSigMessage>());
        Assert.Equal([ClosingSigKind.CloserAndCloseeOutputs], bobSig.Signatures.Kinds);
    }

    #endregion

    #region RBF and reconnection

    [Fact]
    public async Task Given_ClosingChannel_When_FeeBumped_Then_NewClosingCompleteSignedAndStored()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);

        // Act: RBF of Alice's transaction through the IPC close with a feerate
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        await close.Harness.PumpAsync();

        // Assert: a second closing_complete, signed by Bob, stored by both, broadcast by both
        var completes = close.Bob.Received.OfType<ClosingCompleteMessage>().ToList();
        Assert.Equal(2, completes.Count);
        var bumpedFee = ClosingFeeCalculator.FeeSat(10_000, 676);
        Assert.Equal(bumpedFee, (ulong)completes[1].Payload.FeeSatoshis.Satoshi);
        Assert.Equal(2, close.Alice.Received.OfType<ClosingSigMessage>().Count());

        var stored = Transaction.Load(close.Alice.Channel.ClosingTransaction!.RawTxBytes, Network.RegTest);
        Assert.Equal(AliceSat - (long)bumpedFee, CloseHarness.OutputTo(stored, CloseHarness.AliceScript));
        Assert.Equal(close.Alice.Channel.ClosingTransaction.TxId, close.Bob.Channel.ClosingTransaction!.TxId);
        Assert.Equal(3, close.Published(close.Alice).Count);
        Assert.Equal(3, close.Published(close.Bob).Count);
        AssertSimpleClose(close, close.Alice.Channel.ClosingTransaction, TwoNodeHarness.BlockHeight);
    }

    [Fact]
    public async Task Given_OurClosingCompleteUnanswered_When_BumpedAgain_Then_Refused()
    {
        // Arrange: Alice's bump is sent but not delivered yet
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => close.CloseService(close.Alice)
                                     .CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                        new ChannelCloseRequest(FeeRatePerKw: 20_000), ct));

        // Assert: B2-SC-C09
        Assert.Contains("B2-SC-C09", refused.Message);
    }

    [Fact]
    public async Task Given_ClosingChannel_When_Reconnect_Then_ShutdownsResentAndBumpStillWorks()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        var shutdownsBefore = close.Bob.Received.OfType<ShutdownMessage>().Count();

        // Act
        await close.Harness.DisconnectAsync();
        await close.Harness.ReconnectAsync();
        await close.Harness.PumpAsync();
        await close.CloseService(close.Bob)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 5_000), ct);
        await close.Harness.PumpAsync();

        // Assert: B2-RE-28 (our shutdown re-sent), both still Closing, and Bob's RBF signed by Alice
        Assert.Equal(shutdownsBefore + 1, close.Bob.Received.OfType<ShutdownMessage>().Count());
        Assert.Equal(ChannelState.Closing, close.Alice.Channel.State);
        Assert.Equal(ChannelState.Closing, close.Bob.Channel.State);
        var bobCompletes = close.Alice.Received.OfType<ClosingCompleteMessage>().ToList();
        Assert.Equal(2, bobCompletes.Count);
        Assert.Equal(ClosingFeeCalculator.FeeSat(5_000, 676), (ulong)bobCompletes[1].Payload.FeeSatoshis.Satoshi);
        Assert.Equal(2, close.Bob.Received.OfType<ClosingSigMessage>().Count());
    }

    [Fact]
    public async Task Given_ReconnectedBeforeTheShutdownsAreResent_When_FeeBumpRequested_Then_RefusedWithAReason()
    {
        // Arrange - regression: the registry's flag is reset by the new connection and set again only by the peer's
        // shutdown; a bump asked in between used to be dropped silently (the Closing channel only reported)
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        await close.Harness.DisconnectAsync();
        await close.Harness.ReconnectAsync();
        var sentBefore = close.Published(close.Alice).Count;

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => close.CloseService(close.Alice)
                                     .CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                        new ChannelCloseRequest(FeeRatePerKw: 10_000), ct));

        // Assert
        Assert.Contains("option_simple_close is not active", refused.Message);
        Assert.Equal(ChannelState.Closing, close.Alice.Channel.State);
        Assert.Equal(sentBefore, close.Published(close.Alice).Count);
    }

    [Fact]
    public async Task Given_LegacyClosingChannel_When_FeeBumpRequested_Then_RefusedWithAReason()
    {
        // Arrange: without option_simple_close the agreed closing transaction can't be replaced
        using var close = new CloseHarness();
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        Assert.Equal(ChannelState.Closing, close.Alice.Channel.State);

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => close.CloseService(close.Alice)
                                     .CloseChannelAsync(TwoNodeHarness.ChannelId,
                                                        new ChannelCloseRequest(FeeRatePerKw: 10_000), ct));

        // Assert: a request without a feerate still only reports
        Assert.Contains("can't bump its closing fee", refused.Message);
        var result = await close.CloseService(close.Alice)
                                .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(), ct);
        Assert.Equal(ChannelState.Closing, result.State);
    }

    #endregion

    #region closing_complete receiver (B2-SC-E*)

    [Fact]
    public async Task Given_FeeAboveClosersBalance_When_ClosingComplete_Then_WarningAndDisconnect()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, (ulong)AliceSat + 1, CloseHarness.AliceScript,
                                                CloseHarness.BobScript, 600, signFee: 1_000);

        // Act / Assert: B2-SC-E01
        await AssertWarningAsync(close, message, "B2-SC-E01");
    }

    [Fact]
    public async Task Given_CloseeScriptNotOurs_When_ClosingComplete_Then_WarningAndDisconnect()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, 2_000, CloseHarness.AliceScript, s_aliceNewScript, 600);

        // Act / Assert: B2-SC-E02
        await AssertWarningAsync(close, message, "B2-SC-E02");
    }

    [Fact]
    public async Task Given_InvalidCloserScript_When_ClosingComplete_Then_WarningAndDisconnect()
    {
        // Arrange: a P2PKH script is not a shutdown form
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        BitcoinScript p2Pkh = new([0x76, 0xa9, 0x14, .. Enumerable.Repeat((byte)0x11, 20), 0x88, 0xac]);
        var message = CraftAliceClosingComplete(close, 2_000, p2Pkh, CloseHarness.BobScript, 600);

        // Act / Assert: B2-SC-E03
        await AssertWarningAsync(close, message, "B2-SC-E03");
    }

    [Fact]
    public async Task Given_SelectedSignatureMissing_When_ClosingComplete_Then_WarningAndDisconnect()
    {
        // Arrange: only closer_output_only, while Bob's output is not dust (he must check closer_and_closee_outputs,
        // or closee_output_only without it)
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, 2_000, CloseHarness.AliceScript, CloseHarness.BobScript, 600,
                                                kinds: [ClosingSigKind.CloserOutputOnly]);

        // Act / Assert: B2-SC-E07
        await AssertWarningAsync(close, message, "B2-SC-E07");
    }

    [Fact]
    public async Task Given_SignatureForAnotherTransaction_When_ClosingComplete_Then_WarningAndDisconnect()
    {
        // Arrange: signed at another lock time than the one sent
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, 2_000, CloseHarness.AliceScript, CloseHarness.BobScript, 600,
                                                signLockTime: 601);

        // Act / Assert: B2-SC-E08
        await AssertWarningAsync(close, message, "B2-SC-E08");
    }

    [Fact]
    public async Task Given_ValidClosingCompleteWithNewScriptAndLockTime_When_Received_Then_SignedStoredAndScriptUsedLater()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, 3_000, s_aliceNewScript, CloseHarness.BobScript, 777);

        // Act
        await close.Bob.ChannelManager.HandleChannelMessageAsync(message, CloseHarness.SimpleCloseFeatures(),
                                                                 close.Alice.NodeId);
        close.Bob.DropOutbox();

        // Assert: B2-SC-E09 (closing_sig in the same field, fields echoed; the tx stored and broadcast)
        var sig = Assert.Single(close.Bob.Lost.OfType<ClosingSigMessage>());
        Assert.Equal([ClosingSigKind.CloserAndCloseeOutputs], sig.Signatures.Kinds);
        Assert.Equal(s_aliceNewScript, sig.Payload.CloserScriptPubKey);
        Assert.Equal(CloseHarness.BobScript, sig.Payload.CloseeScriptPubKey);
        Assert.Equal(3_000L, sig.Payload.FeeSatoshis.Satoshi);
        Assert.Equal(777U, sig.Payload.LockTime);
        var stored = AssertSimpleClose(close, close.Bob.Channel.ClosingTransaction!, 777);
        Assert.Equal(AliceSat - 3_000, CloseHarness.OutputTo(stored, s_aliceNewScript));
        Assert.Contains(close.Published(close.Bob), t => t.TxId == close.Bob.Channel.ClosingTransaction!.TxId);

        // B2-SC-E10: Bob's next closing_complete pays Alice's new script, and a spend by it is a mutual close
        Assert.Equal(s_aliceNewScript, close.Bob.Channel.RemoteShutdownScript);
        Assert.True(ChannelManager.IsMutualCloseOf(close.Bob.Channel, close.Bob.Channel.ClosingTransaction!));
        await close.CloseService(close.Bob)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 3_000), ct);
        close.Bob.DropOutbox();
        var bobComplete = Assert.Single(close.Bob.Lost.OfType<ClosingCompleteMessage>());
        Assert.Equal(s_aliceNewScript, bobComplete.Payload.CloseeScriptPubKey);
    }

    [Fact]
    public async Task Given_PeerRbfsWithANewScript_When_AnEarlierClosingTxIsChecked_Then_StillAMutualCloseOfTheChannel()
    {
        // Arrange - regression: every transaction Bob signed before Alice's new closer_scriptpubkey pays her old
        // script; whichever of them confirms must still be recognised as a mutual close (ChannelManager records it and
        // the channel reaches Closed)
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var earlier = close.Published(close.Bob).ToList();
        Assert.Equal(2, earlier.Count);
        var message = CraftAliceClosingComplete(close, 3_000, s_aliceNewScript, CloseHarness.BobScript, 777);

        // Act
        await close.Bob.ChannelManager.HandleChannelMessageAsync(message, CloseHarness.SimpleCloseFeatures(),
                                                                 close.Alice.NodeId);
        close.Bob.DropOutbox();

        // Assert
        Assert.Equal(s_aliceNewScript, close.Bob.Channel.RemoteShutdownScript);
        Assert.NotEqual(earlier[0].TxId, close.Bob.Channel.ClosingTransaction!.TxId);
        Assert.NotEqual(earlier[1].TxId, close.Bob.Channel.ClosingTransaction!.TxId);
        Assert.All(earlier, tx => Assert.True(ChannelManager.IsMutualCloseOf(close.Bob.Channel, tx)));
        Assert.True(ChannelManager.IsMutualCloseOf(close.Bob.Channel, close.Bob.Channel.ClosingTransaction!));
    }

    [Fact]
    public async Task Given_OptionNotNegotiated_When_ClosingComplete_Then_WarningWithoutDisconnect()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var message = CraftAliceClosingComplete(close, 2_000, CloseHarness.AliceScript, CloseHarness.BobScript, 600);

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(message, new FeatureOptions(),
                                                                                   close.Alice.NodeId));

        // Assert
        Assert.False(warning.CloseConnection);
    }

    [Fact]
    public async Task Given_SimpleCloseNegotiated_When_ClosingSigned_Then_Warning()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true);
        await CloseAsync(close);
        var closingSigned = new ClosingSignedMessage(
            new ClosingSignedPayload(TwoNodeHarness.ChannelId, LightningMoney.Satoshis(1_000),
                                     new CompactSignature(new byte[64])));

        // Act / Assert: the legacy negotiation does not apply with option_simple_close
        await Assert.ThrowsAsync<ChannelWarningException>(
            () => close.Bob.ChannelManager.HandleChannelMessageAsync(closingSigned, CloseHarness.SimpleCloseFeatures(),
                                                                     close.Alice.NodeId));
    }

    #endregion

    #region closing_sig receiver (B2-SC-G*)

    [Theory]
    [InlineData("fee", "B2-SC-G01")]
    [InlineData("locktime", "B2-SC-G01")]
    [InlineData("two", "B2-SC-G02")]
    [InlineData("notSent", "B2-SC-G03")]
    [InlineData("badSig", "B2-SC-G04")]
    public async Task Given_BadClosingSig_When_Received_Then_WarningAndDisconnect(string fault, string requirementId)
    {
        // Arrange: Alice's RBF closing_complete is outstanding (not delivered)
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        close.Alice.DropOutbox();
        var sent = Assert.Single(close.Alice.Lost.OfType<ClosingCompleteMessage>());
        var fee = (ulong)sent.Payload.FeeSatoshis.Satoshi;
        var lockTime = sent.Payload.LockTime;
        var both = BobSignsAlicesVariant(close, fee, lockTime, ClosingSigKind.CloserAndCloseeOutputs);
        var closerOnly = BobSignsAlicesVariant(close, fee, lockTime, ClosingSigKind.CloserOutputOnly);

        var (payloadFee, payloadLockTime, signatures) = fault switch
        {
            "fee" => (fee + 1, lockTime, ClosingSignatures.Single(ClosingSigKind.CloserAndCloseeOutputs, both)),
            "locktime" => (fee, lockTime + 1, ClosingSignatures.Single(ClosingSigKind.CloserAndCloseeOutputs, both)),
            "two" => (fee, lockTime, new ClosingSignatures(closerOnly, null, both)),
            "notSent" => (fee, lockTime, ClosingSignatures.Single(ClosingSigKind.CloseeOutputOnly, both)),
            _ => (fee, lockTime, ClosingSignatures.Single(ClosingSigKind.CloserAndCloseeOutputs, closerOnly))
        };
        var message = new ClosingSigMessage(
            new ClosingSigPayload(TwoNodeHarness.ChannelId, CloseHarness.AliceScript, CloseHarness.BobScript,
                                  LightningMoney.Satoshis(payloadFee), payloadLockTime), signatures);

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Alice.ChannelManager.HandleChannelMessageAsync(
                              message, CloseHarness.SimpleCloseFeatures(), close.Bob.NodeId));

        // Assert
        Assert.True(warning.CloseConnection);
        Assert.Contains(requirementId, warning.Message);
    }

    [Fact]
    public async Task Given_ValidClosingSigForTheCloserOnlyVariant_When_Received_Then_ThatTransactionStoredAndBroadcast()
    {
        // Arrange: Alice's RBF closing_complete is outstanding; Bob answers the closer_output_only variant
        using var close = new CloseHarness(simpleClose: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        close.Alice.DropOutbox();
        var sent = Assert.Single(close.Alice.Lost.OfType<ClosingCompleteMessage>());
        var fee = (ulong)sent.Payload.FeeSatoshis.Satoshi;
        var signature = BobSignsAlicesVariant(close, fee, sent.Payload.LockTime, ClosingSigKind.CloserOutputOnly);
        var message = new ClosingSigMessage(
            new ClosingSigPayload(TwoNodeHarness.ChannelId, CloseHarness.AliceScript, CloseHarness.BobScript,
                                  sent.Payload.FeeSatoshis, sent.Payload.LockTime),
            ClosingSignatures.Single(ClosingSigKind.CloserOutputOnly, signature));

        // Act
        await close.Alice.ChannelManager.HandleChannelMessageAsync(message, CloseHarness.SimpleCloseFeatures(),
                                                                   close.Bob.NodeId);

        // Assert: B2-SC-G06
        var stored = AssertSimpleClose(close, close.Alice.Channel.ClosingTransaction!, sent.Payload.LockTime);
        Assert.Single(stored.Outputs);
        Assert.Equal(AliceSat - (long)fee, CloseHarness.OutputTo(stored, CloseHarness.AliceScript));
        Assert.Equal(close.Alice.Channel.ClosingTransaction!.TxId, close.Published(close.Alice)[^1].TxId);
    }

    #endregion

    #region Helpers

    private static async Task CloseAsync(CloseHarness close, HarnessNode? initiator = null)
    {
        await close.CloseService(initiator ?? close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                      TestContext.Current.CancellationToken);
        await close.Harness.PumpAsync();
    }

    /// <summary>
    /// A simple-close transaction: spends the funding output with a valid witness, version 2, sequence 0xFFFFFFFD and
    /// the given lock time.
    /// </summary>
    private static Transaction AssertSimpleClose(CloseHarness close, SignedTransaction signed, uint lockTime)
    {
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        Assert.Equal(2U, tx.Version);
        Assert.Equal(lockTime, (uint)tx.LockTime);
        Assert.Equal(0xFFFFFFFDU, (uint)Assert.Single(tx.Inputs).Sequence);
        Assert.True(tx.Inputs.AsIndexedInputs().First().VerifyScript(close.FundingTxOut(), out var error),
                    error.ToString());
        Assert.True(ChannelManager.IsMutualCloseOf(close.Alice.Channel, signed)
                 || ChannelManager.IsMutualCloseOf(close.Bob.Channel, signed));
        return tx;
    }

    /// <summary>
    /// A <c>closing_complete</c> from Alice with the given fields, signed with her real funding key (optionally over
    /// another fee or lock time than the one sent).
    /// </summary>
    private static ClosingCompleteMessage CraftAliceClosingComplete(CloseHarness close, ulong feeSat,
                                                                    BitcoinScript closer, BitcoinScript closee,
                                                                    uint lockTime, ClosingSigKind[]? kinds = null,
                                                                    ulong? signFee = null, uint? signLockTime = null)
    {
        var builder = close.Alice.Services.GetRequiredService<IClosingTransactionBuilder>();
        var terms = new SimpleClosingTerms(close.Alice.Channel.FundingOutput!, AliceMsat, BobMsat, closer, closee,
                                           signFee ?? feeSat, true);
        kinds ??= [ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs];
        var signatures = kinds.ToDictionary(k => k, k => close.Alice.Signer.SignChannelTransaction(
                                                         TwoNodeHarness.ChannelId,
                                                         builder.BuildSimple(terms.Build(k), signLockTime ?? lockTime)));
        return new ClosingCompleteMessage(
            new ClosingCompletePayload(TwoNodeHarness.ChannelId, closer, closee, LightningMoney.Satoshis(feeSat),
                                       lockTime),
            new ClosingSignatures(signatures.GetValueOrDefault(ClosingSigKind.CloserOutputOnly),
                                  signatures.GetValueOrDefault(ClosingSigKind.CloseeOutputOnly),
                                  signatures.GetValueOrDefault(ClosingSigKind.CloserAndCloseeOutputs)));
    }

    /// <summary>Bob's signature of the <paramref name="kind"/> variant of Alice's proposal.</summary>
    private static CompactSignature BobSignsAlicesVariant(CloseHarness close, ulong feeSat, uint lockTime,
                                                          ClosingSigKind kind)
    {
        var builder = close.Bob.Services.GetRequiredService<IClosingTransactionBuilder>();
        var terms = new SimpleClosingTerms(close.Bob.Channel.FundingOutput!, AliceMsat, BobMsat,
                                           CloseHarness.AliceScript, CloseHarness.BobScript, feeSat, false);
        return close.Bob.Signer.SignChannelTransaction(TwoNodeHarness.ChannelId,
                                                       builder.BuildSimple(terms.Build(kind), lockTime));
    }

    private static async Task AssertWarningAsync(CloseHarness close, ClosingCompleteMessage message,
                                                 string requirementId)
    {
        var before = close.Bob.Channel.ClosingTransaction?.TxId;
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                              message, CloseHarness.SimpleCloseFeatures(), close.Alice.NodeId));
        Assert.True(warning.CloseConnection);
        Assert.Contains(requirementId, warning.Message);
        Assert.Equal(before, close.Bob.Channel.ClosingTransaction?.TxId);
    }

    #endregion
}