using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.Safety;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Builders.Interfaces;
using InteractiveTx.TestDoubles;

/// <summary>
/// Dual-funded opens of simple taproot channels (taproot wave t02 lane V2, plan T5; BOLTs PR #1324 as Eclair 0.14.3
/// speaks it): the taproot <c>channel_type</c> in <c>open_channel2</c>/<c>accept_channel2</c>, <c>commit_nonces</c> in
/// every <c>tx_complete</c> once the transaction can be built, the MuSig2 first <c>commitment_signed</c> both ways
/// against those nonces, the MuSig2 P2TR funding output, the peer's next nonce in the first commitment state, the
/// <c>channel_reestablish</c> <c>current_commit_nonce</c> re-sign, and the refusals (no option, public, liquidity ads,
/// RBF, NL-970).
/// </summary>
public class DualFundTaprootTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_BothNodesNegotiateTaproot_When_AliceOpensATaprootDualFundedChannel_Then_ItOpensWithMusig2()
    {
        // Arrange
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);

        // Act
        var result = await OpenTaprootAsync(harness);

        // Assert: a simple taproot channel on both sides, the channel type {80} (+ scid_alias, a private channel)
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var channelId = result.ChannelId;
        var fundingTxId = result.FundingTxId!.Value;
        foreach (var node in harness.Nodes)
        {
            var channel = node.Channel(channelId);
            Assert.True(channel.ChannelParams.OptionSimpleTaproot);
            Assert.Equal(ChannelVersion.V2, channel.Version);
            Assert.False(channel.ChannelParams.AnnounceChannel);
        }

        var open = Assert.IsType<OpenChannel2Message>(harness.Transcript.Single(t => t.Message is OpenChannel2Message)
                                                             .Message);
        var accept = Assert.IsType<AcceptChannel2Message>(
            harness.Transcript.Single(t => t.Message is AcceptChannel2Message).Message);
        Assert.True(TaprootChannelType.IsTaprootChannelType(open.ChannelTypeTlv!.Features));
        Assert.Equal(open.ChannelTypeTlv.Features.GetWireBytes(), accept.ChannelTypeTlv!.Features.GetWireBytes());

        // The funding output is the MuSig2 key-path P2TR output (OP_1 <32 bytes>)
        var session = Assert.Single(await harness.Alice.InScopeAsync(
                                        u => u.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId)));
        Assert.Equal(InteractiveTxSessionState.Signed, session.State);
        var fundingScript = (byte[])session.ConstructedTx!.Outputs[(int)session.ConstructedTx.SharedOutputIndex!.Value]
                                                          .ScriptPubKey;
        Assert.Equal(34, fundingScript.Length);
        Assert.Equal(0x51, fundingScript[0]);
        Assert.Equal(0x20, fundingScript[1]);

        // Every tx_complete sent after the transaction had an output carries commit_nonces, and the last ones of each
        // side are its counter nonces of commitments 0 and 1 bound to the funding txid
        foreach (var node in harness.Nodes)
        {
            var completes = harness.Transcript.Where(t => t.From == node.Name && t.Message is TxCompleteMessage)
                                   .Select(t => (TxCompleteMessage)t.Message)
                                   .ToList();
            var last = completes[^1].CommitNoncesTlv;
            Assert.NotNull(last);
            var signer = node.Services.GetRequiredService<ILightningSigner>();
            Assert.Equal(signer.GetLocalVerificationNonce(channelId, fundingTxId, 0), last.CommitNonce);
            Assert.Equal(signer.GetLocalVerificationNonce(channelId, fundingTxId, 1), last.NextCommitNonce);

            // Commitment 0 of a dual-funded channel is not the v1 open's txid-free nonce
            var keyIndex = node.Channel(channelId).LocalKeySet.KeyIndex;
            Assert.NotEqual(signer.GetLocalVerificationNonce(keyIndex, null, 0), last.CommitNonce);
        }

        // Both first commitment_signed are MuSig2: the zero ECDSA field, a partial signature with its nonce, no HTLC
        var commitments = harness.Transcript.Where(t => t.Message is CommitmentSignedMessage)
                                 .Select(t => (CommitmentSignedMessage)t.Message)
                                 .ToList();
        Assert.Equal(2, commitments.Count);
        Assert.All(commitments, c =>
        {
            Assert.Equal(CommitmentSignatures.ZeroSignature, c.Payload.Signature);
            Assert.NotNull(c.PartialSignatureWithNonceTlv);
            Assert.Empty(c.Payload.HtlcSignatures);
            Assert.Equal(fundingTxId, c.FundingTxIdTlv!.FundingTxId);
        });

        // Each side stored the peer's partial signature of its commitment 0 (persisted with the channel)
        foreach (var node in harness.Nodes)
        {
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(ChannelState.V1FundingSigned, stored!.State);
            var other = harness.Other(node).Name;
            var received = commitments[harness.Transcript.Where(t => t.Message is CommitmentSignedMessage)
                                              .Select(t => t.From).ToList().IndexOf(other)];
            Assert.Equal(received.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce,
                         stored.LastReceivedPartialSignature);
            Assert.Single(node.Published);
        }

        // Act: the funding confirms, channel_ready both ways
        await harness.ConfirmFundingAsync(channelId, fundingTxId);

        // Assert: Open, the first commitment state holds the peer's tx_complete next nonce for this funding
        foreach (var node in harness.Nodes)
        {
            var channel = node.Channel(channelId);
            Assert.True(channel.State == ChannelState.Open, harness.Describe());
            var commitmentsState = Assert.IsType<ChannelCommitments>(channel.Commitments);
            var (_, peerNext) = harness.Transcript.Last(t => t.From == harness.Other(node).Name
                                                          && t.Message is TxCompleteMessage);
            Assert.Equal(((TxCompleteMessage)peerNext).CommitNoncesTlv!.NextCommitNonce,
                         commitmentsState.RemoteNextNonces[fundingTxId]);
            Assert.NotNull(commitmentsState.LocalCommit.RemoteSignatures!.PartialSignature);
        }

        // Our commitment 0 aggregates into a valid key-path witness: the peer signed against our tx_complete nonce
        foreach (var node in harness.Nodes)
        {
            var builder = new LocalCommitmentBroadcastBuilder(
                node.Services.GetRequiredService<ICommitmentTransactionModelFactory>(),
                node.Services.GetRequiredService<ICommitmentTransactionBuilder>(),
                node.Services.GetRequiredService<ILightningSigner>());
            var signed = builder.Build(node.Channel(channelId));
            var tx = NBitcoin.Transaction.Load(signed.Transaction.RawTxBytes, NBitcoin.Network.Main);
            var witness = Assert.Single(tx.Inputs).WitScript;
            Assert.Equal(1, witness.PushCount);
            Assert.Equal(64, witness[0].Length);
        }
    }

    [Fact]
    public async Task Given_TheAccepterContributesNothing_When_TheTransactionGrows_Then_EachTxCompleteHasNewNonces()
    {
        // Arrange: Bob only answers, so he sends tx_complete after each of Alice's additions
        await using var harness = await CreateTaprootHarnessAsync(0);

        // Act
        var result = await OpenTaprootAsync(harness);

        // Assert: Bob's tx_completes once the transaction has an output each carry nonces, different ones for each
        // version of the transaction, and the last ones are the funding's
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var nonces = harness.Transcript.Where(t => t is { From: "Bob", Message: TxCompleteMessage })
                            .Select(t => ((TxCompleteMessage)t.Message).CommitNoncesTlv)
                            .OfType<CommitNoncesTlv>()
                            .ToList();
        Assert.True(nonces.Count >= 2, harness.Describe());
        Assert.Equal(nonces.Count, nonces.Select(n => n.CommitNonce).Distinct().Count());
        Assert.Equal(nonces.Count, nonces.Select(n => n.NextCommitNonce).Distinct().Count());
        var bobSigner = harness.Bob.Services.GetRequiredService<ILightningSigner>();
        Assert.Equal(bobSigner.GetLocalVerificationNonce(result.ChannelId, result.FundingTxId, 0),
                     nonces[^1].CommitNonce);
    }

    [Fact]
    public async Task Given_ThePeerSendsTxCompleteWithoutCommitNonces_When_TheTransactionIsComplete_Then_TxAbort()
    {
        // Arrange: Bob's tx_complete loses its commit_nonces on the way
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);
        harness.Rewrite = (from, message) =>
            from == "Bob" && message is TxCompleteMessage { CommitNoncesTlv: not null } complete
                ? new TxCompleteMessage(complete.Payload)
                : message;

        // Act
        var result = await OpenTaprootAsync(harness);

        // Assert: Alice aborts (Eclair's MissingCommitNonce), signs nothing and forgets the open
        Assert.NotNull(result.FailureReason);
        var (_, abort) = harness.Transcript.First(t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.Contains("MissingCommitNonce",
                        System.Text.Encoding.ASCII.GetString(((TxAbortMessage)abort).Payload.Data));
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: CommitmentSignedMessage });
        Assert.Empty(harness.Alice.Published);
        Assert.Empty(harness.Bob.Published);
    }

    [Fact]
    public async Task Given_AnInvalidPartialSignature_When_TheFirstCommitmentSignedArrives_Then_TxAbort()
    {
        // Arrange: Bob's commitment_signed partial signature is tampered with
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);
        harness.Rewrite = (from, message) =>
        {
            if (from != "Bob" || message is not CommitmentSignedMessage { PartialSignatureWithNonceTlv: { } tlv } cs)
                return message;

            var bytes = tlv.PartialSignatureWithNonce.ToBytes();
            bytes[3] ^= 0x01;
            return new CommitmentSignedMessage(cs.Payload, cs.FundingTxIdTlv,
                                               new PartialSignatureWithNonceTlv(
                                                   new MusigPartialSignatureWithNonce(bytes)));
        };

        // Act
        var result = await OpenTaprootAsync(harness);

        // Assert: refused with tx_abort before Alice's tx_signatures, nothing published
        Assert.NotNull(result.FailureReason);
        var (_, abort) = harness.Transcript.First(t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.Contains("partial signature",
                        System.Text.Encoding.ASCII.GetString(((TxAbortMessage)abort).Payload.Data));
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        Assert.Empty(harness.Alice.Published);
    }

    [Fact]
    public async Task Given_TheLinkDropsBeforeTheSecondCommitmentSigned_When_ANodeRestarts_Then_ItIsSignedAgainFresh()
    {
        // Arrange: stop right before the second commitment_signed is delivered (both sent theirs and stored them)
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);
        _ = harness.Alice.DualFund.OpenAsync(Request(harness), TestContext.Current.CancellationToken);
        var commitments = 0;
        await harness.PumpAsync((_, message) => message is CommitmentSignedMessage && ++commitments == 2);
        var (firstFrom, firstCommitment) = harness.Transcript.First(t => t.Message is CommitmentSignedMessage);
        var channelId = firstCommitment.Payload.ChannelId;
        var receiver = firstFrom == "Alice" ? harness.Alice : harness.Bob;
        var sender = harness.Other(receiver);
        var lost = Assert.IsType<CommitmentSignedMessage>(harness.TakeNext(sender));

        // Act: the node whose peer's commitment_signed was lost restarts from its database, reconnect
        await harness.RestartAsync(receiver);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: its channel_reestablish carries current_commit_nonce (BOLTs PR #1324 type 24), the other's does not
        var reestablishes = harness.Transcript.Where(t => t.Message is ChannelReestablishMessage).ToList();
        var asking = (ChannelReestablishMessage)reestablishes.Single(t => t.From == receiver.Name).Message;
        var answering = (ChannelReestablishMessage)reestablishes.Single(t => t.From == sender.Name).Message;
        Assert.Equal(1, asking.NextFundingTlv!.RetransmitFlags & 1);
        var fundingTxId = new TxId(asking.NextFundingTlv.NextFundingTxId);
        var receiverSigner = receiver.Services.GetRequiredService<ILightningSigner>();
        Assert.Equal(receiverSigner.GetLocalVerificationNonce(channelId, fundingTxId, 0),
                     asking.CurrentCommitNonceTlv!.Nonce);

        // The signer reloaded the channel from the database as dual-funded: the same nonce as its tx_complete before
        var sentBeforeRestart = (TxCompleteMessage)harness.Transcript.Last(t => t.From == receiver.Name
                                                                             && t.Message is TxCompleteMessage)
                                                          .Message;
        Assert.Equal(sentBeforeRestart.CommitNoncesTlv!.CommitNonce, asking.CurrentCommitNonceTlv.Nonce);
        Assert.Null(answering.CurrentCommitNonceTlv);

        // The commitment_signed went out again, signed again with a fresh nonce (never replayed byte for byte)
        var resent = (CommitmentSignedMessage)harness.Transcript.Last(t => t.From == sender.Name
                                                                        && t.Message is CommitmentSignedMessage)
                                                     .Message;
        Assert.NotEqual(lost.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce,
                        resent.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce);

        // The open completed on both sides with one funding transaction, and the channel opens
        foreach (var node in harness.Nodes)
        {
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.True(Assert.Single(sessions).State == InteractiveTxSessionState.Signed, harness.Describe());
            Assert.Single(node.Published);
        }

        await harness.ConfirmFundingAsync(channelId, fundingTxId);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        Assert.NotNull(harness.Alice.Channel(channelId).Commitments!.LocalCommit.RemoteSignatures!.PartialSignature);
        Assert.NotNull(harness.Bob.Channel(channelId).Commitments!.LocalCommit.RemoteSignatures!.PartialSignature);
    }

    [Fact]
    public async Task Given_ATaprootDualFundedOpen_When_EitherSideTriesRbf_Then_ItIsRefused()
    {
        // Arrange
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);
        var result = await OpenTaprootAsync(harness);
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var initRbf = new TxInitRbfMessage(new TxInitRbfPayload(result.ChannelId, 5_000, 500),
                                           new FundingOutputContributionTlv(s_aliceShare));

        // Act
        var bump = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.BumpAsync(result.ChannelId, 5_000, TestContext.Current.CancellationToken));
        await harness.DeliverAsync(harness.Alice, initRbf);
        var reply = harness.TakeNext(harness.Bob);

        // Assert: our bumpopen gets a clear error, the peer's tx_init_rbf a tx_abort with the reason (NL-970)
        Assert.Contains("NL-970", bump.Message);
        var abort = Assert.IsType<TxAbortMessage>(reply);
        Assert.Contains("taproot", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.Equal([result.FundingTxId!.Value], harness.Bob.DualFund.GetSignedFundingTxIds(result.ChannelId));
    }

    [Fact]
    public async Task Given_ATaprootOpen_When_PublicOrNotNegotiatedOrWithLiquidity_Then_OpenAsyncRefusesIt()
    {
        // Arrange
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);
        var request = Request(harness);

        // Act / Assert: never announced, liquidity ads not yet (NL-971)
        var isPublic = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.OpenAsync(request with { IsPublic = true },
                                                   TestContext.Current.CancellationToken));
        Assert.Contains("private", isPublic.Message);
        var liquidity = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.OpenAsync(
                request with { Liquidity = new Domain.LiquidityAds.Models.LiquidityRequest(100_000) },
                TestContext.Current.CancellationToken));
        Assert.Contains("NL-971", liquidity.Message);

        // Without option_simple_taproot on our side
        harness.Alice.Options.Features.OptionSimpleTaproot = FeatureSupport.No;
        var notNegotiated = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.OpenAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("option_simple_taproot", notNegotiated.Message);
        Assert.Empty(harness.Transcript);
    }

    [Fact]
    public async Task Given_TheAccepterDidNotNegotiateTaproot_When_ATaprootOpenChannel2Arrives_Then_ItIsRefused()
    {
        // Arrange: Alice believes taproot is negotiated, the connection's features (Bob's view) say no
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat, TimeSpan.FromSeconds(1));
        harness.NegotiatedFeatures = new FeatureOptions { DualFund = FeatureSupport.Optional };

        // Act
        var result = await OpenTaprootAsync(harness);

        // Assert: Bob answers with an error for the channel, no accept_channel2
        Assert.NotNull(result.FailureReason);
        Assert.Contains(harness.Bob.Errors, e => e.Message.Contains("option_simple_taproot"));
        Assert.DoesNotContain(harness.Transcript, t => t.Message is AcceptChannel2Message);
    }

    [Fact]
    public async Task Given_ANonTaprootDualFundedOpen_When_Negotiated_Then_NoTaprootTlvIsSent()
    {
        // Arrange: taproot negotiated, but the open does not ask for it
        await using var harness = await CreateTaprootHarnessAsync(BobShareSat);

        // Act
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                TestContext.Current.CancellationToken));

        // Assert: an anchors channel, ECDSA commitment_signed, tx_complete without nonces
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        Assert.False(harness.Alice.Channel(result.ChannelId).ChannelParams.OptionSimpleTaproot);
        Assert.All(harness.Transcript.Where(t => t.Message is TxCompleteMessage),
                   t => Assert.Null(((TxCompleteMessage)t.Message).CommitNoncesTlv));
        Assert.All(harness.Transcript.Where(t => t.Message is CommitmentSignedMessage),
                   t => Assert.Null(((CommitmentSignedMessage)t.Message).PartialSignatureWithNonceTlv));
    }

    private static async Task<DualFundHarness> CreateTaprootHarnessAsync(long bobContributionSat,
                                                                         TimeSpan? openTimeout = null)
    {
        var harness = await DualFundHarness.CreateAsync(bobContributionSat, openTimeout);
        harness.NegotiatedFeatures = new FeatureOptions
        {
            DualFund = FeatureSupport.Optional,
            OptionSimpleTaproot = FeatureSupport.Optional,
            AllowExperimentalFeatures = true
        };
        foreach (var node in harness.Nodes)
            node.Options.Features.OptionSimpleTaproot = FeatureSupport.Optional;
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        return harness;
    }

    private static DualFundedOpenRequest Request(DualFundHarness harness) =>
        new(harness.Bob.NodeId, s_aliceShare, 2_500) { SimpleTaproot = true };

    private static Task<DualFundedOpenResult> OpenTaprootAsync(DualFundHarness harness) =>
        harness.RunAsync(harness.Alice.DualFund.OpenAsync(Request(harness), TestContext.Current.CancellationToken));
}