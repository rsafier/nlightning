namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx.Models;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using TestDoubles;

/// <summary>
/// Splicing plan IT4-T3: two production <see cref="Application.InteractiveTx.InteractiveTxDriver"/>s with a test host
/// (a dummy shared funding output) build a transaction over the BOLT 2 interactive-tx messages. Each scenario runs on
/// every available engine (<see cref="InteractiveTxEngines"/>): the reference test engine now, lane IT-A's
/// <see cref="InteractiveTxSession"/> as well once it is merged.
/// </summary>
public class InteractiveTxHarnessTests
{
    private const uint FeeratePerKw = 1_000;

    public static TheoryData<string> Engines => InteractiveTxEngines.All;

    public static TheoryData<string, string> EnginesAndAbortStages
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var engine in InteractiveTxEngines.All)
            {
                foreach (var stage in new[] { "negotiating", "awaiting_commitment_signed", "awaiting_tx_signatures" })
                    data.Add(engine.Data, stage);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_BothSidesContribute_When_Negotiated_Then_BothSignTheSameTransaction(string engine)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);

        // Act
        var first = await harness.StartAsync(FeeratePerKw, ct);
        var undelivered = await harness.PumpAsync(harness.Alice, first, ct);

        // Assert
        Assert.Empty(undelivered);
        var aliceTx = Assert.Single(harness.Alice.Host.Completions);
        var bobTx = Assert.Single(harness.Bob.Host.Completions);
        Assert.Equal(aliceTx.Transaction.TxId, bobTx.Transaction.TxId);
        Assert.Equal(aliceTx.SignedTransaction.RawTxBytes, bobTx.SignedTransaction.RawTxBytes);
        Assert.Equal(2, aliceTx.Transaction.Inputs.Count);
        Assert.Contains(aliceTx.Transaction.Inputs, i => i.AddedBy == InteractiveTxParty.Local);
        Assert.Contains(aliceTx.Transaction.Inputs, i => i.AddedBy == InteractiveTxParty.Remote);
        var shared = Assert.Single(aliceTx.Transaction.Outputs, o => o.IsShared);
        Assert.Equal(LightningMoney.Satoshis(150_000), shared.Amount);
        Assert.Equal(3, aliceTx.Transaction.Outputs.Count);

        // IT-SIG-01: Bob's inputs total less (200k < 300k), so Bob sends tx_signatures first
        var signatures = harness.Transcript.Where(t => t.Message is TxSignaturesMessage).ToList();
        Assert.Equal(new[] { "bob", "alice" }, signatures.Select(s => s.From));

        // IT-SIG-03: each side's tx_signatures follows the peer's commitment_signed
        var aliceCommitment = harness.Transcript.FindIndex(t => t is { From: "alice", Message: TestCommitmentSignedMessage });
        var bobSignatures = harness.Transcript.FindIndex(t => t is { From: "bob", Message: TxSignaturesMessage });
        Assert.True(aliceCommitment >= 0 && aliceCommitment < bobSignatures);

        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            var stored = node.StoredSession(InteractiveTxHarness.ChannelId);
            Assert.NotNull(stored);
            Assert.Equal(InteractiveTxSessionState.Signed, stored.State);
            Assert.True(stored is
            {
                CommitmentSignedSent: true, CommitmentSignedReceived: true, TxSignaturesSent: true,
                TxSignaturesReceived: true
            });
            Assert.Equal(InteractiveTxPurpose.DualFund, stored.Purpose);
            Assert.Empty(node.Contributor.Released);
            Assert.Empty(node.Host.Aborts);
            Assert.False(node.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_CommitmentSignedSent_When_Stored_Then_TheRowPrecedesTheMessageAndSurvivesARestart(
        string engine)
    {
        // Arrange (BOLT 2: remember the negotiation once our commitment_signed is sent)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var first = await harness.StartAsync(FeeratePerKw, ct);

        // Act: stop before the first commitment_signed reaches its peer
        var undelivered = await harness.PumpAsync(harness.Alice, first, ct,
                                                  (_, message) => message is TestCommitmentSignedMessage);

        // Assert: the sender of that commitment_signed has stored the constructed negotiation
        var sender = undelivered[0].From;
        var stored = sender.StoredSession(InteractiveTxHarness.ChannelId);
        Assert.NotNull(stored);
        Assert.Equal(InteractiveTxSessionState.AwaitingCommitmentSigned, stored.State);
        Assert.True(stored.CommitmentSignedSent);
        Assert.False(stored.TxSignaturesSent);
        Assert.NotNull(stored.ConstructedTx);
        Assert.Equal(stored.Inputs.OrderBy(i => i.SerialId).Select(i => i.SerialId),
                     stored.Inputs.Select(i => i.SerialId));

        // Act: the sender restarts (memory lost) and resumes from its row, then the exchange finishes
        sender.Restart();
        Assert.False(sender.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
        await sender.Driver.ResumeAsync(stored,
                                        sender.Terms(InteractiveTxHarness.ChannelId, harness.Other(sender),
                                                     stored.IsInitiator, FeeratePerKw), sender.Host, ct);
        await harness.PumpAsync(undelivered, ct);

        // Assert
        var aliceTx = Assert.Single(harness.Alice.Host.Completions);
        var bobTx = Assert.Single(harness.Bob.Host.Completions);
        Assert.Equal(stored.ConstructedTx.TxId, aliceTx.Transaction.TxId);
        Assert.Equal(aliceTx.Transaction.TxId, bobTx.Transaction.TxId);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_PeerRemovesAndReAddsInputsAndOutputs_When_Completed_Then_OnlyTheCurrentOnesAreBuilt(
        string engine)
    {
        // Arrange: the test plays the initiator by hand against Bob's driver
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        var bob = harness.Bob;
        bob.Fund(200_000);
        await bob.Driver.StartAsync(bob.Terms(InteractiveTxHarness.ChannelId, harness.Alice, false, FeeratePerKw),
                                    bob.Host, ct);
        var aliceUtxo = WalletUtxo.Create(300_000);
        var channelId = InteractiveTxHarness.ChannelId;
        var aliceChange = LightningMoney.Satoshis(190_000);
        IChannelMessage[] script =
        [
            new TxAddInputMessage(new TxAddInputPayload(channelId, 0, aliceUtxo.PrevTx, aliceUtxo.Vout, 0xFFFFFFFD)),
            new TxRemoveInputMessage(new TxRemoveInputPayload(channelId, 0)),
            new TxAddInputMessage(new TxAddInputPayload(channelId, 2, aliceUtxo.PrevTx, aliceUtxo.Vout, 0xFFFFFFFD)),
            new TxAddOutputMessage(new TxAddOutputPayload(LightningMoney.Satoshis(150_000), channelId,
                                                          TestSharedFundingHost.SharedOutputScript, 4)),
            new TxAddOutputMessage(new TxAddOutputPayload(aliceChange, channelId, InteractiveTxMessages.P2WpkhScript,
                                                          6)),
            new TxRemoveOutputMessage(new TxRemoveOutputPayload(channelId, 6)),
            new TxAddOutputMessage(new TxAddOutputPayload(aliceChange, channelId, InteractiveTxMessages.P2WpkhScript,
                                                          8)),
            new TxCompleteMessage(new TxCompletePayload(channelId))
        ];

        // Act
        var replies = new List<IChannelMessage>();
        foreach (var message in script)
            replies.AddRange(await bob.Driver.ReceiveAsync(message, harness.Alice.NodeId, bob.UnitOfWork, ct));

        // Assert
        Assert.DoesNotContain(replies, r => r is TxAbortMessage);
        Assert.Contains(replies, r => r is TestCommitmentSignedMessage);
        var stored = bob.StoredSession(channelId);
        Assert.NotNull(stored);
        Assert.Equal(new[] { 2UL }, stored.Inputs.Where(i => i.AddedBy == InteractiveTxParty.Remote).Select(i => i.SerialId));
        Assert.Equal(aliceUtxo.TxId, stored.Inputs.Single(i => i.SerialId == 2).PrevTxId);
        Assert.Equal(new[] { 4UL, 8UL },
                     stored.Outputs.Where(o => o.AddedBy == InteractiveTxParty.Remote).Select(o => o.SerialId));
        Assert.Contains(stored.ConstructedTx!.Inputs, i => i.SerialId == 2);
        Assert.DoesNotContain(stored.ConstructedTx.Inputs, i => i.SerialId == 0);
        Assert.DoesNotContain(stored.ConstructedTx.Outputs, o => o.SerialId == 6);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_PeerRemovesAnInputItDidNotAdd_When_Received_Then_TxAbortNotAChannelFailure(string engine)
    {
        // Arrange (IT-R-03)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        var bob = harness.Bob;
        bob.Fund(200_000);
        await bob.Driver.StartAsync(bob.Terms(InteractiveTxHarness.ChannelId, harness.Alice, false, FeeratePerKw),
                                    bob.Host, ct);
        var aliceUtxo = WalletUtxo.Create(300_000);
        var channelId = InteractiveTxHarness.ChannelId;
        var bobReply = await bob.Driver.ReceiveAsync(
                           new TxAddInputMessage(new TxAddInputPayload(channelId, 0, aliceUtxo.PrevTx, 0, 0xFFFFFFFD)),
                           harness.Alice.NodeId, bob.UnitOfWork, ct);
        var bobInput = Assert.IsType<TxAddInputMessage>(Assert.Single(bobReply));

        // Act: the initiator removes Bob's own input
        var replies = await bob.Driver.ReceiveAsync(
                          new TxRemoveInputMessage(new TxRemoveInputPayload(channelId, bobInput.Payload.SerialId)),
                          harness.Alice.NodeId, bob.UnitOfWork, ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.False(bob.Driver.IsNegotiating(channelId));
        Assert.Single(bob.Host.Aborts);
        Assert.Single(bob.Contributor.Released);
        Assert.Null(bob.StoredSession(channelId));
    }

    [Theory]
    [MemberData(nameof(EnginesAndAbortStages))]
    public async Task Given_ANegotiationInAnyStateBeforeOurSignatures_When_Aborted_Then_BothSidesForgetIt(
        string engine, string stage)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var first = await harness.StartAsync(FeeratePerKw, ct);
        Func<InteractiveTxTestNode, IChannelMessage, bool> stopBefore = stage switch
        {
            "negotiating" => (_, m) => m is TxAddOutputMessage,
            "awaiting_commitment_signed" => (_, m) => m is TestCommitmentSignedMessage,
            _ => (_, m) => m is TxSignaturesMessage
        };
        var undelivered = await harness.PumpAsync(harness.Alice, first, ct, stopBefore);
        var (sender, _) = undelivered[0];
        var aborter = harness.Other(sender);
        var expectedState = stage switch
        {
            "negotiating" => InteractiveTxSessionState.Negotiating,
            "awaiting_commitment_signed" => InteractiveTxSessionState.AwaitingCommitmentSigned,
            _ => InteractiveTxSessionState.AwaitingTxSignatures
        };
        Assert.Equal(expectedState, aborter.Driver.GetInfo(InteractiveTxHarness.ChannelId)!.State);

        // Act: the side about to receive the next message aborts; the peer's in-flight messages cross our tx_abort
        var abort = await aborter.Driver.AbortAsync(InteractiveTxHarness.ChannelId, "test abort", aborter.UnitOfWork,
                                                    ct);
        await harness.PumpAsync([.. undelivered, .. abort.Select(m => (aborter, m))], ct);

        // Assert
        Assert.IsType<TxAbortMessage>(Assert.Single(abort));
        Assert.False(aborter.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
        Assert.Empty(aborter.Host.Completions);
        Assert.Single(aborter.Host.Aborts);
        Assert.Single(aborter.Contributor.Released);
        if (aborter.StoredSession(InteractiveTxHarness.ChannelId) is { } aborterRow)
            Assert.Equal(InteractiveTxSessionState.Aborted, aborterRow.State);

        if (stage == "awaiting_tx_signatures")
        {
            // IT-ABT-01: the sender already sent tx_signatures, so it keeps the negotiation (no echo, no release)
            Assert.True(sender.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
            Assert.Empty(sender.Contributor.Released);
            Assert.Equal(InteractiveTxSessionState.TxSignaturesSent,
                         sender.StoredSession(InteractiveTxHarness.ChannelId)!.State);
            Assert.Single(harness.Transcript, t => t.Message is TxAbortMessage);
            return;
        }

        // The peer echoed the tx_abort, forgot the negotiation and released its reservation
        Assert.Equal(2, harness.Transcript.Count(t => t.Message is TxAbortMessage));
        Assert.False(sender.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
        Assert.Single(sender.Host.Aborts);
        Assert.Single(sender.Contributor.Released);
        Assert.Null(aborter.Driver.GetInfo(InteractiveTxHarness.ChannelId));
        if (sender.StoredSession(InteractiveTxHarness.ChannelId) is { } senderRow)
            Assert.Equal(InteractiveTxSessionState.Aborted, senderRow.State);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_OurSignaturesSent_When_WeTryToAbort_Then_Refused(string engine)
    {
        // Arrange (IT-ABT-01: MUST NOT send tx_abort after our tx_signatures)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        var first = await harness.StartAsync(FeeratePerKw, ct);
        var undelivered = await harness.PumpAsync(harness.Alice, first, ct, (_, m) => m is TxSignaturesMessage);
        var signer = undelivered[0].From;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.Driver.AbortAsync(InteractiveTxHarness.ChannelId, "too late", signer.UnitOfWork, ct));
        Assert.True(signer.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_ACompletedTransaction_When_RbfAtAHigherFeerate_Then_TheReplacementDoubleSpendsIt(
        string engine)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        var original = Assert.Single(harness.Alice.Host.Completions);
        const uint rbfFeerate = 1_100;
        AcceptRbfReusingInputs(harness.Bob, harness.Alice);

        // Act
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(
                          ReusingTerms(harness.Alice, harness.Bob, true, rbfFeerate),
                          harness.Alice.Host.LocalOutputShare, ct);
        await harness.PumpAsync(harness.Alice, initRbf, ct);

        // Assert
        var sent = Assert.IsType<TxInitRbfMessage>(Assert.Single(initRbf));
        Assert.Equal(rbfFeerate, sent.Payload.Feerate);
        Assert.Single(harness.Transcript, t => t.Message is TxAckRbfMessage);
        Assert.Equal(2, harness.Alice.Host.Completions.Count);
        Assert.Equal(2, harness.Bob.Host.Completions.Count);
        var replacement = harness.Alice.Host.Completions[1];
        Assert.Equal(replacement.Transaction.TxId, harness.Bob.Host.Completions[1].Transaction.TxId);
        Assert.NotEqual(original.Transaction.TxId, replacement.Transaction.TxId);
        Assert.Equal(rbfFeerate, replacement.FeeratePerKw);

        // IT-RBF-01: the replacement spends the original's outpoints, and pays more (smaller change)
        foreach (var input in original.Transaction.Inputs)
            Assert.Contains(replacement.Transaction.Inputs,
                            i => i.PrevTxId == input.PrevTxId && i.PrevTxVout == input.PrevTxVout);
        Assert.True(Change(replacement.Transaction) < Change(original.Transaction));

        // Both attempts are remembered, their reservations kept (either may confirm)
        Assert.Equal(2, harness.Alice.Driver.GetInfo(InteractiveTxHarness.ChannelId)!.CompletedAttempts.Count);
        Assert.Empty(harness.Alice.Contributor.Released);
        Assert.Empty(harness.Bob.Contributor.Released);
        Assert.Equal(2, harness.Alice.Repository.Committed.Values.Count(s => s.State == InteractiveTxSessionState.Signed));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_ALiquidityPurchase_When_Rbf_Then_TheRequestAndTheAnswerAreCarried(string engine)
    {
        // Arrange (liquidity ads, NL-771: an RBF of a purchase keeps requesting and the seller answers again)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        var rate = new FundingRate(10_000, 500_000, 550, 100, 5_000, 1_000);
        var request = new RequestFunding(50_000, rate, LiquidityPaymentDetails.FromChannelBalance);
        var willFund = new WillFund(rate, Convert.FromHexString("0020" + new string('b', 64)),
                                    new CompactSignature(Enumerable.Repeat((byte)0x07, 64).ToArray()));
        harness.Bob.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(ReusingTerms(harness.Bob, harness.Alice, false, message.Payload.Feerate),
                                            harness.Bob.Host.LocalOutputShare, willFund);

        // Act
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(
                          ReusingTerms(harness.Alice, harness.Bob, true, 1_100),
                          harness.Alice.Host.LocalOutputShare, ct, request);
        await harness.PumpAsync(harness.Alice, initRbf, ct);

        // Assert
        var sent = Assert.IsType<TxInitRbfMessage>(Assert.Single(initRbf));
        Assert.Equal(request, sent.RequestFundingTlv?.Request);
        Assert.Equal(request, Assert.Single(harness.Bob.Host.RbfRequests).RequestFundingTlv?.Request);
        var ack = Assert.IsType<TxAckRbfMessage>(
            Assert.Single(harness.Transcript, t => t.Message is TxAckRbfMessage).Message);
        Assert.Equal(willFund, ack.ProvideFundingTlv?.WillFund);
        Assert.Equal(2, harness.Alice.Host.Completions.Count);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_AnRbfWithFreshInputsOnly_When_Completed_Then_AbortedAndTheOriginalKept(string engine)
    {
        // Arrange (IT-RBF-01: an attempt that does not double-spend every previous one fails)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Alice.Fund(310_000);
        harness.Bob.Fund(200_000);
        harness.Bob.Fund(210_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        var firstReservations = new[] { harness.Alice, harness.Bob }
                               .Select(n => n.StoredSession(InteractiveTxHarness.ChannelId)!.LocalContribution
                                             .ReservationId)
                               .ToList();
        harness.Bob.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(
                harness.Bob.Terms(InteractiveTxHarness.ChannelId, harness.Alice, false, message.Payload.Feerate),
                harness.Bob.Host.LocalOutputShare);

        // Act: both sides pick new wallet outputs
        var initRbf = await harness.Alice.Driver.RequestRbfAsync(
                          harness.Alice.Terms(InteractiveTxHarness.ChannelId, harness.Bob, true, 1_100),
                          harness.Alice.Host.LocalOutputShare, ct);
        await harness.PumpAsync(harness.Alice, initRbf, ct);

        // Assert
        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage);
        Assert.Single(harness.Alice.Host.Completions);
        Assert.Single(harness.Bob.Host.Completions);
        foreach (var node in new[] { harness.Alice, harness.Bob })
        {
            Assert.False(node.Driver.IsNegotiating(InteractiveTxHarness.ChannelId));
            Assert.Single(node.Driver.GetInfo(InteractiveTxHarness.ChannelId)!.CompletedAttempts);
            // The RBF attempt's reservation (if the side got as far as making one: an engine may refuse its
            // non-double-spending contribution before the attempt starts) is released, the original's is kept
            Assert.InRange(node.Contributor.Released.Count, 0, 1);
            Assert.All(node.Contributor.Released,
                       released => Assert.DoesNotContain(released, firstReservations.Select(r => r!.Value)));
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_AnRbfBelowTheFeerateFloor_When_Requested_Then_RefusedOrAborted(string engine)
    {
        // Arrange (IT-RBF-01 with the additive 25 sat/kw rule: max(1000 * 25 / 24, 1000 + 25) = 1041)
        var ct = TestContext.Current.CancellationToken;
        var harness = new InteractiveTxHarness(engine, 100_000, 50_000);
        harness.Alice.Fund(300_000);
        harness.Bob.Fund(200_000);
        await harness.PumpAsync(harness.Alice, await harness.StartAsync(FeeratePerKw, ct), ct);
        AcceptRbfReusingInputs(harness.Bob, harness.Alice);

        // Act / Assert: we refuse to send it
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.Driver.RequestRbfAsync(ReusingTerms(harness.Alice, harness.Bob, true, 1_040),
                                                       LightningMoney.Zero, ct));

        // Act / Assert: the recipient answers tx_abort without asking its host
        var replies = await harness.Bob.Driver.ReceiveAsync(
                          new TxInitRbfMessage(new TxInitRbfPayload(InteractiveTxHarness.ChannelId, 1_040, 0)),
                          harness.Alice.NodeId, harness.Bob.UnitOfWork, ct);
        Assert.IsType<TxAbortMessage>(Assert.Single(replies));
        Assert.Empty(harness.Bob.Host.RbfRequests);
        Assert.Single(harness.Bob.Driver.GetInfo(InteractiveTxHarness.ChannelId)!.CompletedAttempts);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Given_ASharedInput_When_Negotiated_Then_BothSharedInputSignaturesMakeItsWitness(string engine)
    {
        // Arrange: a splice-like negotiation: Alice (initiator) spends the 100k shared input and adds 50k of her
        // wallet; Bob contributes nothing (splicing plan D10)
        var ct = TestContext.Current.CancellationToken;
        var sharedUtxo = WalletUtxo.Create(100_000);
        var sharedInput = new SharedFundingInput(sharedUtxo.TxId, 0, sharedUtxo.Amount,
                                                 TestSharedFundingHost.SharedOutputScript, 400);
        var harness = new InteractiveTxHarness(engine, 0, 0);
        SetShares(harness.Alice, sharedInput, 60_000, 40_000, 110_000, 40_000);
        SetShares(harness.Bob, sharedInput, 40_000, 60_000, 40_000, 110_000);
        harness.Alice.Fund(300_000);
        await harness.Bob.Driver.StartAsync(
            harness.Bob.Terms(InteractiveTxHarness.ChannelId, harness.Alice, false, FeeratePerKw, false),
            harness.Bob.Host, ct);

        // Act
        var first = await harness.Alice.Driver.StartAsync(
                        harness.Alice.Terms(InteractiveTxHarness.ChannelId, harness.Bob, true, FeeratePerKw,
                                            walletAmountSat: 50_000), harness.Alice.Host, ct);
        await harness.PumpAsync(harness.Alice, first, ct);

        // Assert
        var completion = Assert.Single(harness.Alice.Host.Completions);
        Assert.Single(harness.Bob.Host.Completions);
        var shared = Assert.Single(completion.Transaction.Inputs, i => i.IsShared);
        Assert.Equal(sharedUtxo.TxId, shared.PrevTxId);
        var signatures = harness.Transcript.Where(t => t.Message is TxSignaturesMessage).ToList();
        Assert.Equal(new[] { "bob", "alice" }, signatures.Select(s => s.From));
        Assert.All(signatures, s => Assert.NotNull(((TxSignaturesMessage)s.Message).SharedInputSignatureTlv));
        Assert.Empty(((TxSignaturesMessage)signatures[0].Message).Payload.Witnesses);
    }

    private static void SetShares(InteractiveTxTestNode node, SharedFundingInput sharedInput, long localIn,
                                  long remoteIn, long localOut, long remoteOut)
    {
        var host = new TestSharedFundingHost
        {
            SharedInput = sharedInput,
            LocalInputShare = LightningMoney.Satoshis(localIn),
            RemoteInputShare = LightningMoney.Satoshis(remoteIn),
            LocalOutputShare = LightningMoney.Satoshis(localOut),
            RemoteOutputShare = LightningMoney.Satoshis(remoteOut)
        };
        node.Host = host;
    }

    private static InteractiveTxTerms ReusingTerms(InteractiveTxTestNode node, InteractiveTxTestNode peer,
                                                   bool isInitiator, uint feeratePerKw)
    {
        var terms = node.Terms(InteractiveTxHarness.ChannelId, peer, isInitiator, feeratePerKw);
        var previous = node.Repository.Committed.Values
                           .Where(s => s.State == InteractiveTxSessionState.Signed)
                           .MaxBy(s => s.CreatedAt)!.LocalContribution;
        return terms with
        {
            ContributionRequest = null,
            Contribution = node.Contributor.ContributeReusing(previous, terms.ContributionRequest!)
        };
    }

    private static void AcceptRbfReusingInputs(InteractiveTxTestNode node, InteractiveTxTestNode peer)
    {
        node.Host.RbfHandler = (message, _) =>
            InteractiveTxRbfDecision.Accept(ReusingTerms(node, peer, false, message.Payload.Feerate),
                                            node.Host.LocalOutputShare);
    }

    private static LightningMoney Change(ConstructedInteractiveTx transaction) =>
        transaction.Outputs.Where(o => o is { IsShared: false, AddedBy: InteractiveTxParty.Local })
                   .Aggregate(LightningMoney.Zero, (sum, o) => sum + o.Amount);
}