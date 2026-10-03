using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Harness;

/// <summary>
/// The cooperative close of a simple taproot channel (NL-877 T5, bolt-simple-taproot.md §RBF Cooperative Close) in
/// process: two real channel managers and two real signers close a taproot channel with <c>option_simple_close</c>,
/// exchanging closee nonces in <c>shutdown</c>, 98-byte partial signatures with the closer's nonce in
/// <c>closing_complete</c> and 32-byte partial signatures with a <c>next_closee_nonce</c> in <c>closing_sig</c>; RBF in
/// both roles rotates the nonces; every transaction is a one-element key-path spend that NBitcoin's interpreter
/// accepts against the P2TR funding output.
/// </summary>
public class SimpleTaprootCloseHarnessTests
{
    private const long AliceSat = (long)(TwoNodeHarness.FundingSatoshis - TwoNodeHarness.PushSatoshis);

    [Fact]
    public async Task Given_TaprootChannel_When_FunderCloses_Then_MusigClosingTransactionsSpendTheTaprootFunding()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);

        // Act
        await CloseAsync(close);

        // Assert: each shutdown carried a valid closee nonce, no ECDSA field nor closing_signed anywhere
        Assert.Equal(ChannelState.Closing, close.Alice.Channel.State);
        Assert.Equal(ChannelState.Closing, close.Bob.Channel.State);
        Assert.NotNull(Assert.Single(close.Bob.Received.OfType<ShutdownMessage>()).ShutdownNonceTlv);
        Assert.NotNull(Assert.Single(close.Alice.Received.OfType<ShutdownMessage>()).ShutdownNonceTlv);
        Assert.Empty(close.Alice.Received.OfType<ClosingSignedMessage>());
        Assert.Empty(close.Bob.Received.OfType<ClosingSignedMessage>());

        var aliceComplete = Assert.Single(close.Bob.Received.OfType<ClosingCompleteMessage>());
        Assert.Empty(aliceComplete.Signatures.Kinds);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs],
                     aliceComplete.PartialSignatures.Kinds);
        var bobSig = Assert.Single(close.Alice.Received.OfType<ClosingSigMessage>());
        Assert.Empty(bobSig.Signatures.Kinds);
        Assert.Single(bobSig.PartialSignatures.Kinds);
        Assert.NotNull(bobSig.NextCloseeNonceTlv);

        // Both transactions (each side's own) broadcast by both, each a valid key-path spend of the P2TR funding
        var alicePublished = close.Published(close.Alice).Select(t => t.TxId).ToHashSet();
        Assert.Equal(2, alicePublished.Count);
        Assert.Equal(alicePublished, close.Published(close.Bob).Select(t => t.TxId).ToHashSet());
        foreach (var published in close.Published(close.Alice))
            AssertKeyPathSpend(close, published.RawTxBytes);
        Assert.Contains(close.Alice.Channel.ClosingTransaction!.TxId, alicePublished);
    }

    [Fact]
    public async Task Given_ClosingTaprootChannel_When_EachSideBumps_Then_NextCloseeNoncesSignTheReplacements()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        var bobFirstNext = Assert.Single(close.Alice.Received.OfType<ClosingSigMessage>()).NextCloseeNonceTlv!.Nonce;

        // Act: Alice bumps (closer again, against Bob's next_closee_nonce), then Bob bumps (against Alice's), then
        // Alice once more (Bob's second next_closee_nonce)
        await BumpAsync(close, close.Alice, 10_000, ct);
        await BumpAsync(close, close.Bob, 12_000, ct);
        await BumpAsync(close, close.Alice, 15_000, ct);

        // Assert: every round signed and aggregated; the closer nonces are fresh, the closee nonces rotate
        var aliceCompletes = close.Bob.Received.OfType<ClosingCompleteMessage>().ToList();
        var bobSigs = close.Alice.Received.OfType<ClosingSigMessage>().ToList();
        Assert.Equal(3, aliceCompletes.Count);
        Assert.Equal(3, bobSigs.Count);
        Assert.Equal(2, close.Bob.Received.OfType<ClosingSigMessage>().Count());
        var bobNexts = bobSigs.Select(m => m.NextCloseeNonceTlv!.Nonce).ToList();
        Assert.Equal(3, bobNexts.Distinct().Count());
        Assert.Equal(bobFirstNext, bobNexts[0]);
        var closerNonces = aliceCompletes.SelectMany(m => m.PartialSignatures.Kinds
                                                         .Select(k => m.PartialSignatures.Get(k)!.Value.PublicNonce))
                                         .ToList();
        Assert.Equal(closerNonces.Count, closerNonces.Distinct().Count());

        var stored = close.Alice.Channel.ClosingTransaction!;
        Assert.Equal(stored.TxId, close.Bob.Channel.ClosingTransaction!.TxId);
        var tx = AssertKeyPathSpend(close, stored.RawTxBytes);
        Assert.Equal(AliceSat - (long)(ulong)aliceCompletes[2].Payload.FeeSatoshis.Satoshi,
                     CloseHarness.OutputTo(tx, CloseHarness.AliceScript));
        Assert.Equal(5, close.Published(close.Alice).Count);
        foreach (var published in close.Published(close.Bob))
            AssertKeyPathSpend(close, published.RawTxBytes);
    }

    [Fact]
    public async Task Given_ClosingTaprootChannel_When_Reconnect_Then_ShutdownsCarryFreshNoncesAndBumpWorks()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        var firstAliceNonce = Assert.Single(close.Bob.Received.OfType<ShutdownMessage>()).ShutdownNonceTlv!.Nonce;

        // Act
        await close.Harness.DisconnectAsync();
        await close.Harness.ReconnectAsync();
        await close.Harness.PumpAsync();
        await BumpAsync(close, close.Bob, 5_000, ct);

        // Assert: the re-sent shutdowns carry new nonces (the old secrets were forgotten) and sign Bob's RBF
        var aliceShutdowns = close.Bob.Received.OfType<ShutdownMessage>().ToList();
        Assert.Equal(2, aliceShutdowns.Count);
        Assert.NotEqual(firstAliceNonce, aliceShutdowns[1].ShutdownNonceTlv!.Nonce);
        Assert.Equal(2, close.Alice.Received.OfType<ClosingCompleteMessage>().Count());
        Assert.Equal(2, close.Bob.Received.OfType<ClosingSigMessage>().Count());
        AssertKeyPathSpend(close, close.Bob.Channel.ClosingTransaction!.RawTxBytes);
        Assert.Equal(close.Alice.Channel.ClosingTransaction!.TxId, close.Bob.Channel.ClosingTransaction.TxId);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_PeerShutdownHasNoNonce_Then_ChannelFailed()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);
        var shutdown = new ShutdownMessage(new ShutdownPayload(TwoNodeHarness.ChannelId, CloseHarness.AliceScript));

        // Act
        var failed = await Assert.ThrowsAsync<ChannelFailedException>(
                         () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                             shutdown, CloseHarness.SimpleCloseFeatures(), close.Alice.NodeId));

        // Assert: as Eclair's MissingClosingNonce; our commitment is broadcast (no cooperative close can be signed)
        Assert.Equal("missing shutdown_nonce", failed.PeerMessage);
        Assert.True(failed.MustBroadcast);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_PeerShutdownNonceIsNotAPoint_Then_ChannelFailed()
    {
        // Arrange
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);
        var shutdown = new ShutdownMessage(new ShutdownPayload(TwoNodeHarness.ChannelId, CloseHarness.AliceScript),
                                           new ShutdownNonceTlv(new MusigPublicNonce(new byte[66])));

        // Act / Assert
        var failed = await Assert.ThrowsAsync<ChannelFailedException>(
                         () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                             shutdown, CloseHarness.SimpleCloseFeatures(), close.Alice.NodeId));
        Assert.Equal("invalid shutdown_nonce", failed.PeerMessage);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_ShutdownWithoutSimpleClose_Then_WarningAndNoLegacyClose()
    {
        // Arrange: a connection that did not negotiate option_simple_close (the spec makes taproot depend on it)
        using var close = new CloseHarness(simpleTaproot: true);
        var nonce = close.Alice.Signer.CreateClosingNonce(TwoNodeHarness.ChannelId);
        var shutdown = new ShutdownMessage(new ShutdownPayload(TwoNodeHarness.ChannelId, CloseHarness.AliceScript),
                                           new ShutdownNonceTlv(nonce));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                              shutdown, CloseHarness.LegacyCloseFeatures(), close.Alice.NodeId));

        // Assert: refused without failing the channel or replying with a legacy shutdown/closing_signed
        Assert.True(warning.CloseConnection);
        Assert.Contains("option_simple_close", warning.PeerMessage);
        Assert.Equal(ChannelState.Open, close.Bob.Channel.State);
        Assert.Null(close.Bob.Channel.RemoteShutdownScript);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_PeerSendsClosingSigned_Then_WarningAndMessageIgnored()
    {
        // Arrange
        using var close = new CloseHarness(simpleTaproot: true);
        var closingSigned = new ClosingSignedMessage(
            new ClosingSignedPayload(TwoNodeHarness.ChannelId, LightningMoney.Satoshis(1_000),
                                     new CompactSignature(new byte[64])));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                              closingSigned, CloseHarness.LegacyCloseFeatures(), close.Alice.NodeId));

        // Assert: a warning (not an error, which would make the peer broadcast), channel untouched
        Assert.False(warning.CloseConnection);
        Assert.Contains("simple taproot", warning.PeerMessage);
        Assert.Equal(ChannelState.Open, close.Bob.Channel.State);
        Assert.Null(close.Bob.Channel.ClosingTransaction);
    }

    [Fact]
    public async Task Given_TamperedClosingCompletePartialSignature_When_Received_Then_WarningAndCloseeNonceKept()
    {
        // Arrange: the channel closed; Alice's RBF closing_complete is taken from her outbox and tampered with
        using var close = new CloseHarness(simpleClose: true, simpleTaproot: true);
        var ct = TestContext.Current.CancellationToken;
        await CloseAsync(close);
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: 10_000), ct);
        Assert.True(close.Alice.TryTakeNext(out var next));
        var held = Assert.IsType<ClosingCompleteMessage>(next);
        var original = held.PartialSignatures.CloserAndCloseeOutputs!.Value;
        var bytes = original.ToBytes();
        bytes[3] ^= 0x01;
        var tampered = new ClosingCompleteMessage(held.Payload, held.Signatures,
                                                  new ClosingPartialSignaturesWithNonce(
                                                      held.PartialSignatures.CloserOutputOnly,
                                                      CloserAndCloseeOutputs: new MusigPartialSignatureWithNonce(bytes)));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => close.Bob.ChannelManager.HandleChannelMessageAsync(
                              tampered, CloseHarness.SimpleCloseFeatures(), close.Alice.NodeId));

        // Assert: B2-SC-E08; the genuine message is still signed (Bob's closee nonce was not spent by the bad one)
        Assert.Contains("B2-SC-E08", warning.Message);
        await close.Bob.ChannelManager.HandleChannelMessageAsync(held, CloseHarness.SimpleCloseFeatures(),
                                                                 close.Alice.NodeId);
        Assert.IsType<ClosingSigMessage>(close.Bob.PeekNext());
        Assert.Equal(ChannelState.Closing, close.Bob.Channel.State);
        AssertKeyPathSpend(close, close.Bob.Channel.ClosingTransaction!.RawTxBytes);
    }

    #region Helpers

    private static async Task CloseAsync(CloseHarness close)
    {
        await close.CloseService(close.Alice)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(),
                                      TestContext.Current.CancellationToken);
        await close.Harness.PumpAsync();
    }

    private static async Task BumpAsync(CloseHarness close, HarnessNode node, uint feeratePerKw,
                                        CancellationToken ct)
    {
        await close.CloseService(node)
                   .CloseChannelAsync(TwoNodeHarness.ChannelId, new ChannelCloseRequest(FeeRatePerKw: feeratePerKw),
                                      ct);
        await close.Harness.PumpAsync();
    }

    /// <summary>
    /// A closing transaction of the taproot channel: one input with a single 64-byte BIP 340 signature (key path,
    /// SIGHASH_DEFAULT), valid by script execution against the P2TR funding output.
    /// </summary>
    private static Transaction AssertKeyPathSpend(CloseHarness close, byte[] rawTx)
    {
        var tx = Transaction.Load(rawTx, Network.RegTest);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(1, input.WitScript.PushCount);
        Assert.Equal(64, input.WitScript[0].Length);
        close.AssertSpendsFunding(tx);
        return tx;
    }

    #endregion
}