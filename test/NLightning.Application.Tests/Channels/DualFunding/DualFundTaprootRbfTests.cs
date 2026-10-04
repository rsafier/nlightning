using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.Safety;
using Application.InteractiveTx;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-970: RBF of a simple taproot dual-funded open, in both directions (our <c>bumpopen</c> and the peer's
/// <c>tx_init_rbf</c>; the opener and the accepter bump). Every signed attempt keeps the peer's MuSig2 partial signature
/// of our commitment 0 on its own funding (<c>InteractiveTxSessions.TheirCommitmentPartialSignature</c>), made against
/// our verification nonce bound to that attempt's txid, so whichever attempt confirms is followed and can be force
/// closed: the stored partial signature aggregated with a fresh signing of ours, a key-path witness valid by script
/// execution against that attempt's funding output.
/// </summary>
public class DualFundTaprootRbfTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private static readonly LightningMoney s_bobShare = LightningMoney.Satoshis(400_000);

    [Theory]
    [InlineData("Alice", true)]
    [InlineData("Alice", false)]
    [InlineData("Bob", true)]
    [InlineData("Bob", false)]
    public async Task Given_ATaprootOpenBumpedTwiceAcrossARestart_When_AnAttemptConfirms_Then_ItIsFollowedAndForceClosable(
        string bumper, bool firstConfirms)
    {
        // Arrange: the open, a bump, a restart of each node, a second bump (three signed attempts)
        await using var harness = await CreateTaprootHarnessAsync();
        var attempts = await OpenAndBumpTwiceAsync(harness, bumper);
        var channelId = attempts[0].ChannelId;
        var confirmed = firstConfirms ? attempts[0].FundingTxId!.Value : attempts[^1].FundingTxId!.Value;

        // Every signed attempt is stored with the peer's partial signature (no ECDSA signature) on both nodes, and
        // no nonce signs twice: every commitment_signed has its own signing nonce, and each attempt's commitment 0
        // has its own verification nonce (bound to its txid)
        foreach (var node in harness.Nodes)
        {
            var signed = await SignedSessionsAsync(node, channelId);
            Assert.Equal(attempts.Select(a => a.FundingTxId!.Value), signed.Select(s => s.ConstructedTx!.TxId));
            Assert.All(signed, s =>
            {
                Assert.NotNull(s.TheirCommitmentPartialSignature);
                Assert.Null(s.TheirCommitmentSignature);
            });

            var signer = node.Services.GetRequiredService<ILightningSigner>();
            var keyIndex = node.Channel(channelId).LocalKeySet.KeyIndex;
            var verificationNonces = attempts.Select(a => signer.GetLocalVerificationNonce(keyIndex, a.FundingTxId, 0))
                                             .ToList();
            Assert.Equal(attempts.Count, verificationNonces.Distinct().Count());
        }

        var signingNonces = harness.Transcript.Where(t => t.Message is CommitmentSignedMessage)
                                   .Select(t => ((CommitmentSignedMessage)t.Message).PartialSignatureWithNonceTlv!
                                                                                     .PartialSignatureWithNonce
                                                                                     .PublicNonce)
                                   .ToList();
        Assert.True(signingNonces.Count >= 2 * attempts.Count, harness.Describe());
        Assert.Equal(signingNonces.Count, signingNonces.Distinct().Count());

        // Act: Bob's chain monitor sees the attempt reach its depth first (Alice keeps his channel_ready until hers)
        await harness.Bob.ConfirmAsync(channelId, confirmed);
        await harness.PumpAsync();

        // Assert: Bob follows it and can force close it before any commitment state exists: commitment 0 from the
        // stored partial signature of that attempt
        Assert.Equal(confirmed, harness.Bob.Channel(channelId).FundingOutput!.TransactionId);
        Assert.Null(harness.Bob.Channel(channelId).Commitments);
        await AssertForceCloseValidAsync(harness.Bob, channelId, confirmed, 0);

        // Act: Alice's monitor sees it too
        await harness.Alice.ConfirmAsync(channelId, confirmed);
        await harness.PumpAsync();

        // Assert: Alice follows it, applies Bob's channel_ready on it, and her commitment 0 force-closes it as well
        var alice = harness.Alice.Channel(channelId);
        Assert.Equal(confirmed, alice.FundingOutput!.TransactionId);
        Assert.True(alice.State == ChannelState.Open, harness.Describe());
        Assert.Contains(confirmed, Assert.IsType<ChannelCommitments>(alice.Commitments).RemoteNextNonces.Keys);
        await AssertForceCloseValidAsync(harness.Alice, channelId, confirmed, 0);
        var stored = await harness.Alice.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(confirmed, stored!.FundingOutput!.TransactionId);
    }

    [Theory]
    [InlineData("Alice", true)]
    [InlineData("Bob", false)]
    public async Task Given_ABumpedTaprootOpen_When_AnAttemptConfirms_Then_BothFollowItPayBothWaysAndCanForceClose(
        string bumper, bool firstConfirms)
    {
        // Arrange
        await using var harness = await CreateTaprootHarnessAsync();
        var attempts = await OpenAndBumpTwiceAsync(harness, bumper);
        var channelId = attempts[0].ChannelId;
        var confirmed = firstConfirms ? attempts[0].FundingTxId!.Value : attempts[^1].FundingTxId!.Value;

        // Act
        await harness.ConfirmFundingAsync(channelId, confirmed);

        // Assert: open on the confirmed attempt with its balances, payments both ways over MuSig2 commitments
        foreach (var node in harness.Nodes)
        {
            var channel = node.Channel(channelId);
            Assert.True(channel.State == ChannelState.Open, harness.Describe());
            Assert.Equal(confirmed, channel.FundingOutput!.TransactionId);
            Assert.Equal(node == harness.Alice ? s_aliceShare : s_bobShare, channel.LocalBalance);
        }

        await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(30_000));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, channelId, LightningMoney.Satoshis(20_000));
        await harness.PumpAsync();
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Single(harness.Bob.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.Errors);
        Assert.Empty(harness.Bob.Errors);

        // ...and Alice's latest commitment spends the confirmed attempt's funding output with a valid key-path witness
        await AssertForceCloseValidAsync(harness.Alice, channelId, confirmed,
                                         harness.Alice.Channel(channelId).Commitments!.LocalCommit.Number);
    }

    [Fact]
    public async Task Given_ARestartDuringATaprootRbfAttempt_When_ThePeerForgotIt_Then_BackOnTheSignedAttemptsAndFollowed()
    {
        // Arrange: two signed attempts, then a third that only one node constructed (its commitment_signed stored it
        // and moved its channel to it) before the link drops
        await using var harness = await CreateTaprootHarnessAsync();
        var open = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(Request(harness),
                                                                           TestContext.Current.CancellationToken));
        Assert.True(open.FailureReason is null, $"{open.FailureReason}\n{harness.Describe()}");
        var channelId = open.ChannelId;
        var second = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                             TestContext.Current.CancellationToken));
        Assert.True(second.FailureReason is null, $"{second.FailureReason}\n{harness.Describe()}");
        List<TxId> signed = [open.FundingTxId!.Value, second.FundingTxId!.Value];
        var third = harness.Alice.DualFund.BumpAsync(channelId, 7_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, _) => harness.Nodes.Any(n => n.Channel(channelId).FundingOutput!.TransactionId
                                                                   != signed[1]));
        var constructed = harness.Nodes.Single(n => n.Channel(channelId).FundingOutput!.TransactionId != signed[1]);
        var pending = constructed.Channel(channelId).FundingOutput!.TransactionId!.Value;

        // Act: the node that stored the attempt restarts; the reconnection's next_funding names an attempt the other
        // node does not know
        await harness.RestartAsync(constructed);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        if (constructed == harness.Bob)
            Assert.NotNull((await third.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
                              .FailureReason);

        // Assert: next_local_nonces carry every signed attempt (and the restarted node's unsigned one), the forgotten
        // attempt is aborted, nothing fails, and both nodes are back on the latest signed attempt
        foreach (var node in harness.Nodes)
        {
            var reestablish = (ChannelReestablishMessage)harness.Transcript.Last(
                t => t.From == node.Name && t.Message is ChannelReestablishMessage).Message;
            var entries = reestablish.NextLocalNoncesTlv!.Nonces.Entries.Select(e => e.FundingTxId).ToList();
            Assert.All(signed, txId => Assert.Contains(txId, entries));
            Assert.Equal(node == constructed, entries.Contains(pending));
        }

        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage);
        foreach (var node in harness.Nodes)
        {
            Assert.Empty(node.Errors);
            Assert.Equal(ChannelState.V1FundingSigned, node.Channel(channelId).State);
            Assert.Equal(signed[1], node.Channel(channelId).FundingOutput!.TransactionId);
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(signed[1], stored!.FundingOutput!.TransactionId);
        }

        // ...and the first attempt, confirming, is followed and opens the channel
        await harness.ConfirmFundingAsync(channelId, signed[0]);
        foreach (var node in harness.Nodes)
        {
            Assert.True(node.Channel(channelId).State == ChannelState.Open, harness.Describe());
            Assert.Equal(signed[0], node.Channel(channelId).FundingOutput!.TransactionId);
        }

        await AssertForceCloseValidAsync(harness.Bob, channelId, signed[0], 0);
    }

    [Fact]
    public async Task Given_ARowSignedWithoutAPartialSignature_When_EitherSideBumps_Then_TheRbfIsRefused()
    {
        // Arrange: an open signed by an older build (its attempt row has no partial signature, before migration
        // AddDualFundTaprootAttempts): a replacement could not be followed if the original confirmed
        await using var harness = await CreateTaprootHarnessAsync();
        var open = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(Request(harness),
                                                                           TestContext.Current.CancellationToken));
        Assert.True(open.FailureReason is null, $"{open.FailureReason}\n{harness.Describe()}");
        foreach (var node in harness.Nodes)
        {
            await node.InScopeAsync(async u =>
            {
                var session = Assert.Single(await u.InteractiveTxSessionDbRepository.GetByChannelIdAsync(open.ChannelId));
                await u.InteractiveTxSessionDbRepository.UpdateAsync(session with
                {
                    TheirCommitmentPartialSignature = null
                });
                await u.SaveChangesAsync();
                return 0;
            });
        }

        var initRbf = new TxInitRbfMessage(new Domain.Protocol.Payloads.TxInitRbfPayload(open.ChannelId, 5_000, 500),
                                           new Domain.Protocol.Tlv.FundingOutputContributionTlv(s_aliceShare));

        // Act
        var bump = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.BumpAsync(open.ChannelId, 5_000, TestContext.Current.CancellationToken));
        await harness.DeliverAsync(harness.Alice, initRbf);
        var reply = harness.TakeNext(harness.Bob);

        // Assert: our bumpopen gets the reason, the peer's tx_init_rbf a tx_abort
        Assert.Contains("partial signature", bump.Message);
        var abort = Assert.IsType<TxAbortMessage>(reply);
        Assert.Contains("partial signature", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.Equal([open.FundingTxId!.Value], harness.Bob.DualFund.GetSignedFundingTxIds(open.ChannelId));
    }

    [Fact]
    public async Task Given_ATaprootOpenWithSixteenSignedAttempts_When_EitherSideBumps_Then_RefusedAndReestablishFits()
    {
        // Arrange (NL-1060): the open and 15 bumps, 16 signed attempts, every one an entry of our next_local_nonces
        await using var harness = await CreateTaprootHarnessAsync();
        var open = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(Request(harness),
                                                                           TestContext.Current.CancellationToken));
        Assert.True(open.FailureReason is null, $"{open.FailureReason}\n{harness.Describe()}");
        var channelId = open.ChannelId;
        var feerate = 2_500u;
        for (var i = 0; i < FundingNonces.MaxEntries - 1; i++)
        {
            feerate = (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(feerate);
            var bumped = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(
                                                    channelId, feerate, TestContext.Current.CancellationToken));
            Assert.True(bumped.FailureReason is null, $"bump {i + 1}: {bumped.FailureReason}\n{harness.Describe()}");
        }

        Assert.Equal(FundingNonces.MaxEntries, harness.Bob.DualFund.GetSignedFundingTxIds(channelId).Count);
        feerate = (uint)InteractiveTxDriver.GetMinimumRbfFeeratePerKw(feerate);
        var initRbf = new TxInitRbfMessage(new Domain.Protocol.Payloads.TxInitRbfPayload(channelId, feerate, 500),
                                           new Domain.Protocol.Tlv.FundingOutputContributionTlv(s_aliceShare));

        // Act
        var bump = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, feerate,
                                                                    TestContext.Current.CancellationToken)));
        await harness.DeliverAsync(harness.Alice, initRbf);
        var reply = harness.TakeNext(harness.Bob);

        // Assert: refused both ways, and each side's channel_reestablish still carries all 16 nonces
        Assert.Contains("next_local_nonces", bump.Message);
        var abort = Assert.IsType<TxAbortMessage>(reply);
        Assert.Contains("next_local_nonces", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        foreach (var node in harness.Nodes)
        {
            var reestablish = (ChannelReestablishMessage)harness.Transcript.Last(
                t => t.From == node.Name && t.Message is ChannelReestablishMessage).Message;
            Assert.Equal(FundingNonces.MaxEntries, reestablish.NextLocalNoncesTlv!.Nonces.Count);
        }
    }

    /// <summary>
    /// Opens a taproot channel (Alice 600,000 sat, Bob 400,000 sat), <paramref name="bumper"/> bumps it, both nodes
    /// restart one after the other, and <paramref name="bumper"/> bumps it again: the three attempts, oldest first.
    /// </summary>
    private static async Task<List<DualFundedOpenResult>> OpenAndBumpTwiceAsync(DualFundHarness harness,
                                                                                string bumper)
    {
        var open = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(Request(harness),
                                                                           TestContext.Current.CancellationToken));
        Assert.True(open.FailureReason is null, $"{open.FailureReason}\n{harness.Describe()}");
        var channelId = open.ChannelId;
        var node = bumper == "Alice" ? harness.Alice : harness.Bob;

        var second = await harness.RunAsync(node.DualFund.BumpAsync(channelId, 5_000,
                                                                    TestContext.Current.CancellationToken));
        Assert.True(second.FailureReason is null, $"{second.FailureReason}\n{harness.Describe()}");

        // A restart between the attempts: each node comes back from its database (rows, signer, negotiation)
        foreach (var restarted in harness.Nodes)
        {
            await harness.RestartAsync(restarted);
            await harness.ReconnectAsync();
            await harness.PumpAsync();
        }

        var third = await harness.RunAsync(node.DualFund.BumpAsync(channelId, 7_000,
                                                                   TestContext.Current.CancellationToken));
        Assert.True(third.FailureReason is null, $"{third.FailureReason}\n{harness.Describe()}");

        List<DualFundedOpenResult> attempts = [open, second, third];
        Assert.Equal(3, attempts.Select(a => a.FundingTxId).Distinct().Count());
        foreach (var each in harness.Nodes)
            Assert.Equal(third.FundingTxId, each.Channel(channelId).FundingOutput!.TransactionId);
        return attempts;
    }

    /// <summary>
    /// The node's local commitment <paramref name="number"/>, signed for broadcast (MuSig2: the peer's stored partial
    /// signature with ours), spends <paramref name="fundingTxId"/>'s funding output with a 64-byte key-path witness
    /// that passes script execution.
    /// </summary>
    private static async Task AssertForceCloseValidAsync(DualFundNode node, ChannelId channelId, TxId fundingTxId,
                                                         ulong number)
    {
        var session = (await SignedSessionsAsync(node, channelId)).Single(s => s.ConstructedTx!.TxId == fundingTxId);
        var output = session.ConstructedTx!.Outputs[(int)session.ConstructedTx.SharedOutputIndex!.Value];
        var fundingTxOut = new NBitcoin.TxOut(NBitcoin.Money.Satoshis((long)output.Amount.Satoshi),
                                              new NBitcoin.Script((byte[])output.ScriptPubKey));

        var builder = new LocalCommitmentBroadcastBuilder(
            node.Services.GetRequiredService<ICommitmentTransactionModelFactory>(),
            node.Services.GetRequiredService<ICommitmentTransactionBuilder>(),
            node.Services.GetRequiredService<ILightningSigner>());
        var signed = builder.Build(node.Channel(channelId));
        Assert.Equal(number, signed.CommitmentNumber);

        var tx = NBitcoin.Transaction.Load(signed.Transaction.RawTxBytes, NBitcoin.Network.RegTest);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(fundingTxId, new TxId(input.PrevOut.Hash.ToBytes()));
        Assert.Equal(session.ConstructedTx.SharedOutputIndex.Value, input.PrevOut.N);
        Assert.Equal(1, input.WitScript.PushCount);
        Assert.Equal(64, input.WitScript[0].Length);
        var error = tx.CreateValidator([fundingTxOut]).ValidateInput(0).Error;
        Assert.True(error is null, error?.ToString());
    }

    private static async Task<List<InteractiveTxSessionModel>> SignedSessionsAsync(DualFundNode node,
                                                                                  ChannelId channelId) =>
        (await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId)))
       .Where(s => s.State == InteractiveTxSessionState.Signed)
       .OrderBy(s => s.CreatedAt)
       .ToList();

    private static async Task<DualFundHarness> CreateTaprootHarnessAsync()
    {
        var harness = await DualFundHarness.CreateAsync((long)s_bobShare.Satoshi, allowRbf: true);
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
}