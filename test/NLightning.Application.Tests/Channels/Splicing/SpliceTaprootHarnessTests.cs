using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Tlv;
using Harness;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// NL-965 (taproot wave t03 lane SPL, plan T5 "Splicing"): splices of a simple taproot channel on the real engine and
/// signer (<see cref="SpliceHarness"/> with <c>realEngine</c> and <c>simpleTaproot</c>): BOLTs PR #1324's
/// <c>tx_complete</c> <c>commit_nonces</c>/<c>funding_nonce</c>, the MuSig2 shared input in <c>tx_signatures</c>
/// (verified by script execution against the previous funding output), the P2TR funding output of the rotated keys,
/// partial-signature commitments on every funding, the lock, payments and force closes on the new funding.
/// </summary>
public class SpliceTaprootHarnessTests
{
    private const uint CltvExpiry = 700;
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Fact]
    public async Task Given_TaprootChannel_When_AliceSplicesInAndItLocks_Then_EveryStepUsesMusig2AndTheChannelWorks()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        var utxo = harness.Alice.Fund(500_000);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!;
        var localNumber = harness.Alice.Node.State.LocalCommit.Number;

        // Act: the splice
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert: the negotiation carried the PR #1324 nonces and signatures, nothing ECDSA
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var fundingTx2 = result.SpliceTxId!.Value;
        var messages = harness.Transcript.Select(t => t.Message).ToList();
        var lastCompletes = new[] { "Alice", "Bob" }
                           .Select(n => harness.Transcript.Last(t => t.From == n && t.Message is TxCompleteMessage))
                           .Select(t => (TxCompleteMessage)t.Message)
                           .ToList();
        Assert.All(lastCompletes, c =>
        {
            Assert.NotNull(c.CommitNoncesTlv);
            Assert.NotNull(c.FundingNonceTlv);
        });
        Assert.All(messages.OfType<TxSignaturesMessage>(), s =>
        {
            Assert.Null(s.SharedInputSignatureTlv);
            Assert.NotNull(s.SharedInputPartialSignatureTlv);
        });
        var spliceCommitments = messages.OfType<CommitmentSignedMessage>().ToList();
        Assert.Equal(2, spliceCommitments.Count);
        Assert.All(spliceCommitments, c =>
        {
            Assert.True(c.Payload.Signature.IsZero);
            Assert.NotNull(c.PartialSignatureWithNonceTlv);
            Assert.Equal(fundingTx2, c.FundingTxIdTlv!.FundingTxId);
        });

        // The splice transaction spends the P2TR funding by key path (script-executed) into the P2TR output of the
        // rotated keys, the same bytes on both sides
        Assert.Equal(harness.Alice.Broadcasts.Single().RawTransaction, harness.Bob.Broadcasts.Single().RawTransaction);
        var spliceTx = Transaction.Load(harness.Alice.Broadcasts.Single().RawTransaction, Network.RegTest);
        AssertSharedInputVerifies(harness, spliceTx, fundingTx1, utxo);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var pending = Assert.Single(node.Node.State.PendingFundings);
            Assert.Equal(fundingTx2, pending.FundingTxId);
            Assert.True(pending.LocalFundingKeyIndex >= 1, "a taproot splice rotates the funding key");
            Assert.Equal(localNumber, node.Node.State.LocalCommit.Number);
            Assert.True(node.Node.State.RemoteNextNonces.ContainsKey(fundingTx2), node.Name);
            Assert.True(node.Node.State.RemoteNextNonces.ContainsKey(fundingTx1.FundingTxId), node.Name);
            var output = spliceTx.Outputs[pending.OutputIndex];
            Assert.Equal(TwoNodeHarness.FundingSatoshis + 100_000, (ulong)output.Value.Satoshi);
            Assert.True(output.ScriptPubKey.IsScriptType(ScriptType.Taproot), "the new funding is a P2TR output");
        }

        // Act: payments while the splice is pending (a batch of two partial-signature commitments each way)
        var mark = harness.Transcript.Count;
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 30_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);

        // Assert
        Assert.Empty(harness.Failures);
        var batchMembers = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<CommitmentSignedMessage>()
                                  .ToList();
        Assert.Equal(8, batchMembers.Count);
        Assert.All(batchMembers, c => Assert.NotNull(c.PartialSignatureWithNonceTlv));
        Assert.Equal(4, batchMembers.Count(c => c.FundingTxIdTlv!.FundingTxId == fundingTx2));
        Assert.All(harness.Transcript.Skip(mark).Select(t => t.Message).OfType<RevokeAndAckMessage>(),
                   r => Assert.Equal(2, r.NextLocalNoncesTlv!.Nonces.Entries.Count));

        // Act: the splice confirms on both sides and locks
        mark = harness.Transcript.Count;
        await harness.ConfirmAsync(fundingTx2, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);

        // Assert
        Assert.Equal(["Alice:SpliceLocked", "Bob:SpliceLocked"], harness.Sequence().Skip(mark).ToList());
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var state = node.Node.State;
            Assert.Empty(state.PendingFundings);
            Assert.Equal(fundingTx2, state.Params.Funding!.FundingTxId);
            Assert.Equal(fundingTx2, node.Node.Channel.FundingOutput!.TransactionId);
            Assert.True(node.Node.Channel.FundingOutput.IsSimpleTaproot);
        }

        // Act: payments after the lock (a single commitment on the new funding)
        mark = harness.Transcript.Count;
        var (id2, preimage2) = await OfferAsync(harness, harness.Bob, 20_000_000, 2);
        await FulfillAsync(harness, harness.Alice, id2, preimage2);

        // Assert
        Assert.Empty(harness.Failures);
        var afterLock = harness.Transcript.Skip(mark).Select(t => t.Message).ToList();
        Assert.DoesNotContain(afterLock, m => m is StartBatchMessage);
        Assert.All(afterLock.OfType<CommitmentSignedMessage>(), c =>
        {
            Assert.Equal(fundingTx2, c.FundingTxIdTlv!.FundingTxId);
            Assert.NotNull(c.PartialSignatureWithNonceTlv);
        });

        // Act: Alice force closes after the lock
        var channel = harness.Alice.Node.Channel;
        var broadcast = CreateBroadcastBuilder(harness.Alice).Build(channel);

        // Assert: the commitment spends the locked splice output by MuSig2 key path (script-executed)
        var commitment = Transaction.Load(broadcast.Transaction.RawTxBytes, Network.RegTest);
        var input = Assert.Single(commitment.Inputs);
        var spliceIndex = harness.Alice.Node.State.Params.Funding!.OutputIndex;
        Assert.Equal(new OutPoint(spliceTx.GetHash(), spliceIndex), input.PrevOut);
        Assert.Single(input.WitScript.Pushes);
        Assert.Null(commitment.CreateValidator([spliceTx.Outputs[spliceIndex]]).ValidateInput(0).Error);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_BobSplicesOut_Then_SignedAndLockedWithThePeerAsInitiator()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        var fundingTx1 = harness.Bob.Node.State.Params.Funding!;
        var bobBefore = harness.Bob.Node.State.LocalBalanceMsat;

        // Act: Bob (who opened no funds of his own here) splices out of his balance after receiving a payment
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 200_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);
        var result = await harness.SpliceAsync(harness.Bob, -50_000);

        // Assert
        Assert.True(result.State == SpliceNegotiationState.Signed, $"{result.State}: {result.FailureReason}");
        Assert.Empty(harness.Failures);
        var spliceTxId = result.SpliceTxId!.Value;
        var spliceTx = Transaction.Load(harness.Bob.Broadcasts.Single().RawTransaction, Network.RegTest);
        AssertSharedInputVerifies(harness, spliceTx, fundingTx1);
        Assert.Contains(spliceTx.Outputs, o => o.ScriptPubKey.ToBytes().AsSpan()
                                                     .SequenceEqual((byte[])harness.Bob.Destination.Script)
                                            && o.Value == Money.Satoshis(50_000));

        // Act: locked, then used
        await harness.ConfirmAsync(spliceTxId, TwoNodeHarness.BlockHeight + 3, harness.Bob, harness.Alice);
        var (id2, preimage2) = await OfferAsync(harness, harness.Bob, 10_000_000, 2);
        await FulfillAsync(harness, harness.Alice, id2, preimage2);

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Equal(spliceTxId, node.Node.State.Params.Funding!.FundingTxId);
        Assert.True(harness.Bob.Node.State.LocalBalanceMsat < bobBefore + 200_000_000 - 50_000_000);
    }

    [Fact]
    public async Task Given_APendingTaprootSplice_When_Bumped_Then_TheBumpLocksWithItsOwnNonces()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true,
                                              configureSplice: (_, o) => o.MinRbfInterval = TimeSpan.Zero);
        var utxo = harness.Alice.Fund(500_000);
        var fundingTx1 = harness.Alice.Node.State.Params.Funding!;
        var first = await harness.SpliceAsync(harness.Alice, 100_000);
        Assert.True(first.State == SpliceNegotiationState.Signed, first.FailureReason);
        var mark = harness.Transcript.Count;

        // Act: Alice bumps it
        var bump = harness.Alice.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId,
                                                                         SpliceHarness.FeeratePerKw * 2),
                                                   TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);
        var bumped = await bump;

        // Assert: a second attempt, its own funding nonces and commit nonces, both pending
        Assert.True(bumped.State == SpliceNegotiationState.Signed, $"{bumped.State}: {bumped.FailureReason}");
        Assert.Empty(harness.Failures);
        var rbfTxId = bumped.SpliceTxId!.Value;
        Assert.NotEqual(first.SpliceTxId, rbfTxId);
        var firstNonces = harness.Transcript.Take(mark).Select(t => t.Message).OfType<TxCompleteMessage>()
                                 .Where(c => c.FundingNonceTlv is not null).Select(c => c.FundingNonceTlv!.Nonce)
                                 .ToHashSet();
        var rbfCompletes = harness.Transcript.Skip(mark).Select(t => t.Message).OfType<TxCompleteMessage>().ToList();
        Assert.NotEmpty(rbfCompletes);
        Assert.All(rbfCompletes, c => Assert.DoesNotContain(c.FundingNonceTlv!.Nonce, firstNonces));
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(2, node.Node.State.PendingFundings.Count);
            Assert.True(node.Node.State.RemoteNextNonces.ContainsKey(rbfTxId), node.Name);
        }

        var rbfTx = Transaction.Load(harness.Alice.Broadcasts.Single(b => b.TransactionId == rbfTxId).RawTransaction,
                                     Network.RegTest);
        AssertSharedInputVerifies(harness, rbfTx, fundingTx1, utxo);

        // Act: a payment while both attempts are pending (a batch of three), then the bump confirms and locks
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 10_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);
        await harness.ConfirmAsync(rbfTxId, TwoNodeHarness.BlockHeight + 3, harness.Alice, harness.Bob);
        var (id2, preimage2) = await OfferAsync(harness, harness.Bob, 10_000_000, 2);
        await FulfillAsync(harness, harness.Alice, id2, preimage2);

        // Assert
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Empty(node.Node.State.PendingFundings);
            Assert.Equal(rbfTxId, node.Node.State.Params.Funding!.FundingTxId);
        }
    }

    [Fact]
    public async Task Given_APendingTaprootSplice_When_TheAccepterBumpsIt_Then_ItIsTheInteractiveTxInitiatorAndItLocks()
    {
        // Arrange: Alice splices in, Bob (who contributed nothing) bumps it (BOLT 2: any quiescence initiator may RBF)
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true,
                                              configureSplice: (_, o) => o.MinRbfInterval = TimeSpan.Zero);
        harness.Alice.Fund(500_000);
        var first = await harness.SpliceAsync(harness.Alice, 100_000);
        Assert.True(first.State == SpliceNegotiationState.Signed, first.FailureReason);

        // Act
        var bump = harness.Bob.Service.BumpAsync(new SpliceBumpRequest(TwoNodeHarness.ChannelId,
                                                                       SpliceHarness.FeeratePerKw * 2),
                                                 TestContext.Current.CancellationToken);
        await harness.PumpAsync(bump);
        var bumped = await bump;

        // Assert
        Assert.True(bumped.State == SpliceNegotiationState.Signed, $"{bumped.State}: {bumped.FailureReason}");
        Assert.Empty(harness.Failures);
        var rbfTxId = bumped.SpliceTxId!.Value;
        await harness.ConfirmAsync(rbfTxId, TwoNodeHarness.BlockHeight + 3, harness.Bob, harness.Alice);
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 10_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);
        Assert.Empty(harness.Failures);
        foreach (var node in new[] { harness.Alice, harness.Bob })
            Assert.Equal(rbfTxId, node.Node.State.Params.Funding!.FundingTxId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ATaprootSplice_When_ThePeersSharedInputPartialSignatureIsBadOrMissing_Then_TheChannelFails(
        bool missing)
    {
        // Arrange (BOLTs PR #1324: "If shared_input_partial_signature is not set / not a valid partial signature ...:
        // MUST send an error and fail the channel"); Bob signs first, his tx_signatures is altered on the way
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        harness.Alice.Fund(500_000);
        harness.Bob.Node.Rewrite = m => m is TxSignaturesMessage { SharedInputPartialSignatureTlv: { } tlv } signatures
                                            ? new TxSignaturesMessage(signatures.Payload, null,
                                                                      missing ? null : Corrupt(tlv))
                                            : m;
        var start = harness.Alice.Service.StartAsync(
            new SpliceRequest(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw),
            TestContext.Current.CancellationToken);

        // Act
        for (var round = 0; round < 1_000 && !harness.Failures.Any(f => f.Exception is ChannelFailedException); round++)
        {
            await harness.PumpAsync();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        // Assert: Alice's tx_signatures never goes out, so the splice can never be broadcast
        var failure = Assert.IsType<ChannelFailedException>(
            Assert.Single(harness.Failures, f => f.Exception is ChannelFailedException).Exception);
        Assert.Equal("SP-SIG-01", failure.RequirementId);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        Assert.Empty(harness.Alice.Broadcasts);
        Assert.False(start.IsCompletedSuccessfully && (await start).State == SpliceNegotiationState.Signed);
    }

    [Fact]
    public async Task Given_AFailedTaprootChannelWithAPendingSplice_When_TheSpliceConfirmed_Then_OurCommitmentOnItIsValid()
    {
        // Arrange (SP2-C-T2 on a taproot channel): the splice is signed and pending, then the channel fails
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        harness.Alice.Fund(500_000);
        var result = await harness.SpliceAsync(harness.Alice, 100_000);
        var spliceTxId = result.SpliceTxId!.Value;
        var channel = harness.Alice.Node.Channel;
        var splice = channel.Commitments!.PendingFundings.Single(f => f.FundingTxId == spliceTxId);
        var failure = CreateFailureService(harness.Alice);
        channel.UpdateState(ChannelState.Failed);

        // Act
        var outcome = await failure.BroadcastOnSpliceAsync(channel.ChannelId, spliceTxId,
                                                           TestContext.Current.CancellationToken);

        // Assert: our commitment on the splice output, a MuSig2 key-path spend that verifies against it
        Assert.NotNull(outcome.CommitmentTxId);
        var row = Assert.Single(harness.Alice.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        var commitment = Transaction.Load(row.RawTransaction, Network.RegTest);
        var spliceTx = Transaction.Load(harness.Alice.Broadcasts.Single(b => b.TransactionId == spliceTxId)
                                              .RawTransaction, Network.RegTest);
        Assert.Equal(new OutPoint(spliceTx.GetHash(), splice.OutputIndex), commitment.Inputs.Single().PrevOut);
        Assert.Null(commitment.CreateValidator([spliceTx.Outputs[splice.OutputIndex]]).ValidateInput(0).Error);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_LiquidityIsBoughtWithASplice_Then_RefusedBeforeAnythingIsSent()
    {
        // Arrange
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        harness.Alice.Fund(500_000);

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => harness.Alice.Service.StartAsync(
                              new SpliceRequest(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw)
                              {
                                  Liquidity = new Domain.LiquidityAds.Models.LiquidityRequest(50_000)
                              }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("NL-971", refused.Message);
        Assert.Empty(harness.Transcript);
        Assert.Empty(harness.Alice.Contributor.ActiveReservations);
    }

    [Fact]
    public async Task Given_ATaprootSplice_When_ThePeersTxCompleteHasNoFundingNonce_Then_TxAbortAndTheChannelGoesOn()
    {
        // Arrange: Bob's tx_complete messages lose their funding_nonce on the way to Alice
        using var harness = new SpliceHarness(realEngine: true, simpleTaproot: true);
        harness.Alice.Fund(500_000);
        harness.Bob.Node.Rewrite = m => m is TxCompleteMessage complete
                                            ? new TxCompleteMessage(complete.Payload, complete.CommitNoncesTlv)
                                            : m;

        // Act
        var result = await harness.SpliceAsync(harness.Alice, 100_000);

        // Assert
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        var abort = harness.Transcript.Where(t => t.From == "Alice").Select(t => t.Message).OfType<TxAbortMessage>()
                           .First();
        Assert.Contains("MissingFundingNonce", System.Text.Encoding.ASCII.GetString(abort.Payload.Data.ToArray()));
        Assert.DoesNotContain(harness.Transcript, t => t.From == "Alice" && t.Message is CommitmentSignedMessage);
        Assert.DoesNotContain(harness.Transcript, t => t.Message is TxSignaturesMessage);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.Equal(ChannelState.Open, node.Node.Channel.State);
            Assert.Empty(node.Node.State.PendingFundings);
        }

        // Bob's splice commitment_signed crossed Alice's tx_abort and was taken as a normal one (a warning that closes
        // the connection, NL-1058, any channel type); after the reconnection the channel is used
        Assert.All(harness.Failures, f => Assert.Equal("Alice", f.Node));
        await harness.Harness.DisconnectAsync();
        await harness.Harness.ReconnectAsync();
        await harness.PumpAsync();
        var failures = harness.Failures.Count;
        var bobBefore = harness.Bob.Node.State.LocalBalanceMsat;
        var (id, preimage) = await OfferAsync(harness, harness.Alice, 10_000_000, 1);
        await FulfillAsync(harness, harness.Bob, id, preimage);
        Assert.Equal(failures, harness.Failures.Count);
        Assert.Equal(bobBefore + 10_000_000, harness.Bob.Node.State.LocalBalanceMsat);
    }

    /// <summary>The partial signature with its first byte changed (a signature that does not verify).</summary>
    private static SharedInputPartialSignatureTlv Corrupt(SharedInputPartialSignatureTlv tlv)
    {
        var bytes = tlv.PartialSignatureWithNonce.ToBytes();
        bytes[31] ^= 0x01;
        return new SharedInputPartialSignatureTlv(new MusigPartialSignatureWithNonce(bytes));
    }

    /// <summary>
    /// The splice's shared input (input spending <paramref name="previous"/>) verified by script execution against the
    /// previous P2TR funding output, with every spent output in the BIP 341 sighash.
    /// </summary>
    private static void AssertSharedInputVerifies(SpliceHarness harness, Transaction spliceTx,
                                                  Domain.Channels.Splicing.ChannelFunding previous,
                                                  InteractiveTx.TestDoubles.WalletUtxo? wallet = null)
    {
        var previousOutPoint = new OutPoint(new uint256((byte[])previous.FundingTxId), previous.OutputIndex);
        var musig2 = harness.Alice.Node.Services.GetRequiredService<IMusig2Service>();
        var previousScript = new Script(musig2.AggregateTaprootKeyPath(previous.LocalFundingPubKey,
                                                                       previous.RemoteFundingPubKey)
                                              .GetTaprootScriptPubKey());
        var spent = spliceTx.Inputs.Select(i => i.PrevOut == previousOutPoint
                                                    ? new TxOut(Money.Satoshis((long)previous.CapacitySatoshis),
                                                                previousScript)
                                                    : new TxOut(Money.Satoshis(wallet!.Amount.Satoshi),
                                                                new Script((byte[])wallet.Script)))
                            .ToArray();
        var index = spliceTx.Inputs.FindIndexedInput(previousOutPoint)!.Index;
        Assert.Single(spliceTx.Inputs[index].WitScript.Pushes);
        Assert.Null(spliceTx.CreateValidator(spent).ValidateInput((int)index).Error);
    }

    private static LocalCommitmentBroadcastBuilder CreateBroadcastBuilder(SpliceNode node)
    {
        var services = node.Node.Services;
        return new LocalCommitmentBroadcastBuilder(services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                                   services.GetRequiredService<ICommitmentTransactionBuilder>(),
                                                   services.GetRequiredService<ILightningSigner>());
    }

    private static ChannelFailureService CreateFailureService(SpliceNode node)
    {
        var services = node.Node.Services;
        var errors = new Mock<IChannelErrorSender>();
        return new ChannelFailureService(node.Node.ChainMonitor.Object, errors.Object,
                                         services.GetRequiredService<IChannelLockProvider>(),
                                         services.GetRequiredService<IChannelMemoryRepository>(),
                                         CreateBroadcastBuilder(node),
                                         services.GetRequiredService<ILightningSigner>(),
                                         NullLogger<ChannelFailureService>.Instance,
                                         services.GetRequiredService<IServiceScopeFactory>(), null,
                                         services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                                         services.GetRequiredService<ICommitmentTransactionBuilder>());
    }

    private static async Task<(ulong Id, Secret Preimage)> OfferAsync(SpliceHarness harness, SpliceNode from,
                                                                      ulong amountMsat, int tag)
    {
        var preimage = TwoNodeHarness.Preimage(tag);
        var hash = TwoNodeHarness.Hash(preimage);
        var id = await from.Node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId,
                                                           LightningMoney.MilliSatoshis(amountMsat), hash, CltvExpiry,
                                                           s_onion, null, HtlcOrigin.Local(hash),
                                                           TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        return (id, preimage);
    }

    private static async Task FulfillAsync(SpliceHarness harness, SpliceNode by, ulong id, Secret preimage)
    {
        await by.Node.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id, preimage,
                                                  TestContext.Current.CancellationToken);
        await harness.PumpAsync();
    }
}